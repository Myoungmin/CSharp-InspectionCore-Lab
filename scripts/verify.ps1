[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess) { throw 'Verification requires Windows x64 PowerShell.' }
. (Join-Path $PSScriptRoot 'process.ps1')
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$powershell = Join-Path $PSHOME 'powershell.exe'
$verificationId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$outputDir = Join-Path $repoRoot "artifacts/verification/$verificationId"
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$summary = [ordered]@{ verificationId = $verificationId; status = 'Failed'; baselineCommit = $null; sourceHash = $null; testsPassed = 0; smokeCases = @(); steps = @(); error = $null }

function Get-SourceSnapshot {
    $paths = @(& git -C $repoRoot -c core.quotepath=false ls-files --cached --others --exclude-standard | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0 -or $paths.Count -eq 0) { throw 'Cannot enumerate source files through Git.' }
    $entries = foreach ($path in $paths) {
        $fullPath = Join-Path $repoRoot $path
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Missing tracked source: $path" }
        [ordered]@{ path = $path; sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash }
    }
    $manifest = ConvertTo-Json -InputObject @($entries) -Depth 5 -Compress
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifest))).Replace('-', '') }
    finally { $sha.Dispose() }
    [pscustomobject]@{ Hash = $hash; Manifest = $manifest }
}

function Invoke-Step {
    param([string]$Name, [string]$Executable, [string[]]$ProcessArguments, [int]$ExpectedExitCode = 0, [int]$TimeoutSeconds = 120)
    Write-Host "Verify: $Name"
    $result = Invoke-RecordedProcess -FilePath $Executable -Arguments $ProcessArguments -WorkingDirectory $repoRoot -LogPrefix (Join-Path $outputDir $Name) -TimeoutSeconds $TimeoutSeconds
    $summary.steps += [ordered]@{ name = $Name; exitCode = $result.ExitCode; expectedExitCode = $ExpectedExitCode }
    if ($result.ExitCode -ne $ExpectedExitCode) {
        throw "$Name returned $($result.ExitCode), expected $ExpectedExitCode. See $outputDir/$Name.*.log"
    }
    return $result
}

try {
    $commit = & git -C $repoRoot rev-parse --verify --quiet HEAD
    if ($LASTEXITCODE -eq 0) { $summary.baselineCommit = $commit }
    $before = Get-SourceSnapshot
    $summary.sourceHash = $before.Hash
    [IO.File]::WriteAllText((Join-Path $outputDir 'source-manifest.json'), $before.Manifest, [Text.UTF8Encoding]::new($false))
    $null = Invoke-Step 'restore' $dotnet @('restore', 'InspectionLab.sln', '--locked-mode')
    $null = Invoke-Step 'build' $dotnet @('build', 'InspectionLab.sln', '--configuration', 'Release', '--no-restore')
    $testDir = Join-Path $outputDir 'tests'
    $null = Invoke-Step 'mstest' $dotnet @('test', 'tests/Inspection.Tests/Inspection.Tests.csproj', '--configuration', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=core.trx', '--results-directory', $testDir, '--blame-hang-timeout', '30s')
    $trxPath = Join-Path $testDir 'core.trx'
    if (-not (Test-Path -LiteralPath $trxPath)) { throw 'MSTest did not produce the required TRX.' }
    [xml]$trx = [IO.File]::ReadAllText($trxPath)
    $counters = $trx.TestRun.ResultSummary.Counters
    $testResults = @($trx.TestRun.Results.UnitTestResult)
    if ([int]$counters.total -lt 19 -or [int]$counters.executed -ne [int]$counters.total -or
        [int]$counters.passed -ne [int]$counters.total -or @($testResults | Where-Object { $_.outcome -ne 'Passed' }).Count -gt 0) {
        throw 'M1 requires at least 19 executed, passing tests with no skipped or failed results.'
    }
    $summary.testsPassed = [int]$counters.passed

    $hostDll = Join-Path $repoRoot 'src/Inspection.Host/bin/Release/net9.0/Inspection.Host.dll'
    $resultDir = Join-Path $outputDir 'results with spaces'
    $seenIds = @()
    foreach ($scenario in @('pass', 'fail', 'pass')) {
        $index = $seenIds.Count
        $hostResult = Invoke-Step "host-$index-$scenario" $dotnet @($hostDll, '--scenario', $scenario, '--output', $resultDir) -TimeoutSeconds 30
        $runMatch = [regex]::Match($hostResult.Stdout, 'RunId=([0-9a-fA-F-]{36})')
        if (-not $runMatch.Success -or $hostResult.Stdout -notmatch 'Status=Succeeded') { throw 'Host did not report a successful run with RunId.' }
        $runId = [Guid]::Parse($runMatch.Groups[1].Value)
        if ($runId -eq [Guid]::Empty -or $runId -in $seenIds) { throw 'RunId must be nonempty and unique.' }
        $seenIds += $runId
        $resultPath = Join-Path $resultDir ($runId.ToString('N') + '.json')
        if (-not (Test-Path -LiteralPath $resultPath)) { throw 'Host succeeded without publishing a result file.' }
        $data = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $expectedVerdict = if ($scenario -eq 'pass') { 'Pass' } else { 'Fail' }
        $expectedScore = if ($scenario -eq 'pass') { 100 } else { 75 }
        $expectedDefects = if ($scenario -eq 'pass') { 0 } else { 1 }
        if ([Guid]$data.RunId -ne $runId -or $data.JobId -ne "demo-$scenario" -or
            $data.Assessment.Verdict -cne $expectedVerdict -or $data.Assessment.Score -ne $expectedScore -or
            $data.Assessment.DefectCount -ne $expectedDefects -or $data.Assessment.SampleCount -ne 4 -or
            [DateTimeOffset]$data.InspectedAtUtc -lt [DateTimeOffset]$data.StartedAtUtc) {
            throw "Unexpected persisted result for $scenario."
        }
        $summary.smokeCases += [ordered]@{ scenario = $scenario; runId = $runId.ToString(); verdict = $expectedVerdict; score = $expectedScore }
    }
    if (@(Get-ChildItem -LiteralPath $resultDir -Filter '*.json').Count -ne 3 -or @(Get-ChildItem -LiteralPath $resultDir -Filter '*.tmp').Count -ne 0) {
        throw 'Unexpected result count or unfinished temporary files.'
    }

    $blockedPath = Join-Path $outputDir 'not-a-directory'
    [IO.File]::WriteAllText($blockedPath, 'storage failure fixture')
    $storageFailure = Invoke-Step 'host-storage-failure' $dotnet @($hostDll, '--output', $blockedPath) -ExpectedExitCode 1 -TimeoutSeconds 30
    if ($storageFailure.Stderr -notmatch 'RunId=[0-9a-fA-F-]{36} Status=Faulted Stage=Persist ComputedScore=100\.00' -or $storageFailure.Stdout -match 'Succeeded') {
        throw 'Storage failure must preserve the computed score and report Faulted at Persist.'
    }
    $null = Invoke-Step 'host-invalid-arguments' $dotnet @($hostDll, '--scenario', 'unknown') -ExpectedExitCode 2 -TimeoutSeconds 30
    $null = Invoke-Step 'architecture' $powershell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'check-architecture.ps1'))
    $null = Invoke-Step 'documentation' $powershell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'build-docs.ps1'))
    $after = Get-SourceSnapshot
    if ($after.Hash -ne $before.Hash) { throw 'Source changed during verification; rerun against the final content.' }
    $summary.status = 'Passed'
}
catch {
    $summary.error = $_.Exception.Message
    throw
}
finally {
    $summaryPath = Join-Path $outputDir 'summary.json'
    [IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Write-Host "Verification $($summary.status): $summaryPath"
}
