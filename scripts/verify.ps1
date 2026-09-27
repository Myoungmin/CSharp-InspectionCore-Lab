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
$summary = [ordered]@{ milestone = 'M3c'; verificationId = $verificationId; status = 'Failed'; baselineCommit = $null; sourceHash = $null; nativeDllHash = $null; testsPassed = 0; integrationTestsPassed = 0; nativeTestsPassed = 0; smokeCases = @(); steps = @(); error = $null }

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
    $null = Invoke-Step 'build-native' $powershell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'build-native.ps1'))
    $nativeDll = Join-Path $repoRoot 'artifacts/native/Release/NativeInspection.dll'
    $summary.nativeDllHash = (Get-FileHash -LiteralPath $nativeDll -Algorithm SHA256).Hash
    # dotnet MSBuild cannot build vcxproj. Build the managed entrypoints and their references explicitly.
    $managedEntrypoints = [ordered]@{
        'host' = 'src/Inspection.Host/Inspection.Host.csproj'
        'core-tests' = 'tests/Inspection.Tests/Inspection.Tests.csproj'
        'integration-tests' = 'tests/Inspection.IntegrationTests/Inspection.IntegrationTests.csproj'
    }
    foreach ($entry in $managedEntrypoints.GetEnumerator()) {
        $null = Invoke-Step "restore-$($entry.Key)" $dotnet @('restore', $entry.Value, '--locked-mode')
        $null = Invoke-Step "build-$($entry.Key)" $dotnet @('build', $entry.Value, '--configuration', 'Release', '--no-restore')
    }
    $testDir = Join-Path $outputDir 'tests'
    $null = Invoke-Step 'mstest' $dotnet @('test', 'tests/Inspection.Tests/Inspection.Tests.csproj', '--configuration', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=core.trx', '--results-directory', $testDir, '--blame-hang-timeout', '30s')
    $trxPath = Join-Path $testDir 'core.trx'
    if (-not (Test-Path -LiteralPath $trxPath)) { throw 'MSTest did not produce the required TRX.' }
    [xml]$trx = [IO.File]::ReadAllText($trxPath)
    $counters = $trx.TestRun.ResultSummary.Counters
    $testResults = @($trx.TestRun.Results.UnitTestResult)
    if ([int]$counters.total -lt 83 -or [int]$counters.executed -ne [int]$counters.total -or
        [int]$counters.passed -ne [int]$counters.total -or @($testResults | Where-Object { $_.outcome -ne 'Passed' }).Count -gt 0) {
        throw 'M3c requires at least 83 executed, passing Core tests (52 existing + 31 auto cases), with no skipped or failed results.'
    }
    $summary.testsPassed = [int]$counters.passed

    $integrationDir = Join-Path $outputDir 'integration-tests'
    $null = Invoke-Step 'integration-mstest' $dotnet @('test', 'tests/Inspection.IntegrationTests/Inspection.IntegrationTests.csproj', '--configuration', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=integration.trx', '--results-directory', $integrationDir, '--blame-hang-timeout', '30s')
    $integrationTrxPath = Join-Path $integrationDir 'integration.trx'
    if (-not (Test-Path -LiteralPath $integrationTrxPath)) { throw 'Required integration TRX was not produced.' }
    [xml]$integrationTrx = [IO.File]::ReadAllText($integrationTrxPath)
    $integrationCounters = $integrationTrx.TestRun.ResultSummary.Counters
    $integrationResults = @($integrationTrx.TestRun.Results.UnitTestResult)
    if ([int]$integrationCounters.total -lt 37 -or [int]$integrationCounters.executed -ne [int]$integrationCounters.total -or
        [int]$integrationCounters.passed -ne [int]$integrationCounters.total -or $integrationResults.Count -ne [int]$integrationCounters.total -or
        @($integrationResults | Where-Object { $_.outcome -ne 'Passed' }).Count -gt 0) {
        throw 'M3c requires at least 37 integration tests, all executed and passing; skip is not permitted.'
    }
    $nativeTestIds = @($integrationTrx.TestRun.TestDefinitions.UnitTest | Where-Object { $_.TestMethod.className -in @('Inspection.IntegrationTests.NativeInspectorTests', 'Inspection.IntegrationTests.NativeLifetimeTests', 'Inspection.IntegrationTests.NativeAutoTests') } | ForEach-Object { $_.id })
    $nativeResults = @($integrationResults | Where-Object { $_.testId -in $nativeTestIds })
    if ($nativeResults.Count -lt 34) { throw 'The 34 mandatory actual-DLL test cases were not executed.' }
    $summary.integrationTestsPassed = [int]$integrationCounters.passed
    $summary.nativeTestsPassed = $nativeResults.Count
    $null = Invoke-Step 'required-test-cases' $powershell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'check-required-tests.ps1'), '-CoreTrx', $trxPath, '-IntegrationTrx', $integrationTrxPath)
    foreach ($copy in @('src/Inspection.Host/bin/Release/net9.0/NativeInspection.dll', 'tests/Inspection.IntegrationTests/bin/Release/net9.0/NativeInspection.dll')) {
        if ((Get-FileHash -LiteralPath (Join-Path $repoRoot $copy) -Algorithm SHA256).Hash -ne $summary.nativeDllHash) { throw "Stale native DLL deployed to $copy" }
    }

    $hostDll = Join-Path $repoRoot 'src/Inspection.Host/bin/Release/net9.0/Inspection.Host.dll'
    $resultDir = Join-Path $outputDir 'results with spaces'
    $seenIds = @()
    foreach ($inspector in @('managed', 'native')) {
      foreach ($scenario in @('pass', 'fail', 'pass')) {
        $index = $seenIds.Count
        $hostResult = Invoke-Step "host-$index-$inspector-$scenario" $dotnet @($hostDll, '--scenario', $scenario, '--inspector', $inspector, '--output', $resultDir) -TimeoutSeconds 30
        $runMatch = [regex]::Match($hostResult.Stdout, 'RunId=([0-9a-fA-F-]{36})')
        if (-not $runMatch.Success -or $hostResult.Stdout -notmatch 'Status=Succeeded' -or $hostResult.Stdout -notmatch "Inspector=$inspector") { throw 'Host did not report a successful run with RunId and the selected inspector.' }
        if ($inspector -eq 'native') {
            $progressMatches = @([regex]::Matches($hostResult.Stdout, '(?m)^NativeProgress=(\d+)/4\r?$'))
            $progress = @($progressMatches | ForEach-Object { $_.Groups[1].Value })
            if (($progress -join ',') -cne '0,1,2,3,4' -or $progressMatches[-1].Index -gt $runMatch.Index) {
                throw 'Native Host progress must be ordered and complete before reporting success.'
            }
        }
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
        $summary.smokeCases += [ordered]@{ inspector = $inspector; scenario = $scenario; runId = $runId.ToString(); verdict = $expectedVerdict; score = $expectedScore }
      }
    }
    if (@(Get-ChildItem -LiteralPath $resultDir -Filter '*.json').Count -ne 6 -or @(Get-ChildItem -LiteralPath $resultDir -Filter '*.tmp').Count -ne 0) {
        throw 'Unexpected result count or unfinished temporary files.'
    }

    $blockedPath = Join-Path $outputDir 'not-a-directory'
    $seenAutoIds = @()
    foreach ($inspector in @('managed', 'native')) {
      foreach ($scenario in @('pass', 'fail')) {
        $autoDir = Join-Path $outputDir "auto $inspector $scenario"
        $autoResult = Invoke-Step "host-auto-$inspector-$scenario" $dotnet @($hostDll, '--inspector', $inspector, '--scenario', $scenario, '--repeat', '3', '--interval-ms', '0', '--output', $autoDir) -TimeoutSeconds 30
        $autoMatch = [regex]::Match($autoResult.Stdout, 'AutoId=([0-9a-fA-F-]{36}) State=Completed Reason=RunLimitReached StartedRuns=3 CompletedRuns=3')
        if (-not $autoMatch.Success) { throw 'Auto Host must complete all three sequential runs, including product Fail.' }
        $autoId = [Guid]::Parse($autoMatch.Groups[1].Value)
        if ($autoId -eq [Guid]::Empty -or $autoId -in $seenAutoIds) { throw 'AutoId must be nonempty and unique.' }
        $seenAutoIds += $autoId
        $files = @(Get-ChildItem -LiteralPath $autoDir -Filter '*.json')
        if ($files.Count -ne 3 -or @(Get-ChildItem -LiteralPath $autoDir -Filter '*.tmp').Count -ne 0) { throw 'Auto must publish exactly three finished result files.' }
        $autoRunIds = @()
        foreach ($file in $files) {
            $data = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
            $runId = [Guid]::Parse($data.RunId)
            $expectedVerdict = if ($scenario -eq 'pass') { 'Pass' } else { 'Fail' }
            $expectedScore = if ($scenario -eq 'pass') { 100 } else { 75 }
            $expectedDefects = if ($scenario -eq 'pass') { 0 } else { 1 }
            if ($runId -eq [Guid]::Empty -or $runId -in $seenIds -or $file.BaseName -cne $runId.ToString('N') -or
                $data.JobId -cne "demo-$scenario" -or $data.Assessment.Verdict -cne $expectedVerdict -or
                $data.Assessment.Score -ne $expectedScore -or $data.Assessment.DefectCount -ne $expectedDefects -or $data.Assessment.SampleCount -ne 4) {
                throw 'Auto result must retain JobId, have a new RunId and match the selected scenario.'
            }
            $seenIds += $runId
            $autoRunIds += $runId.ToString()
        }
        $lastRun = [regex]::Match($autoResult.Stdout, 'RunId=([0-9a-fA-F-]{36}) JobId=demo-\w+ Status=Succeeded')
        if (-not $lastRun.Success -or $lastRun.Groups[1].Value -notin $autoRunIds) { throw 'Auto Host must report its last persisted run.' }
        if ($inspector -eq 'native' -and @([regex]::Matches($autoResult.Stdout, '(?m)^NativeProgress=4/4\r?$')).Count -ne 3) { throw 'Each native auto run must finish its progress callbacks.' }
        $summary.smokeCases += [ordered]@{ inspector = $inspector; scenario = $scenario; autoId = $autoId.ToString(); runIds = $autoRunIds; count = 3 }
      }
    }
    [IO.File]::WriteAllText($blockedPath, 'storage failure fixture')
    $storageFailure = Invoke-Step 'host-storage-failure' $dotnet @($hostDll, '--output', $blockedPath) -ExpectedExitCode 1 -TimeoutSeconds 30
    if ($storageFailure.Stderr -notmatch 'RunId=[0-9a-fA-F-]{36} Status=Faulted Stage=Persist ComputedScore=100\.00' -or $storageFailure.Stdout -match 'Succeeded') {
        throw 'Storage failure must preserve the computed score and report Faulted at Persist.'
    }
    $null = Invoke-Step 'host-invalid-arguments' $dotnet @($hostDll, '--scenario', 'unknown') -ExpectedExitCode 2 -TimeoutSeconds 30
    $null = Invoke-Step 'host-invalid-inspector' $dotnet @($hostDll, '--inspector', 'unknown') -ExpectedExitCode 2 -TimeoutSeconds 30
    $null = Invoke-Step 'host-invalid-timeout' $dotnet @($hostDll, '--timeout-ms', '-2') -ExpectedExitCode 2 -TimeoutSeconds 30
    $null = Invoke-Step 'host-invalid-repeat' $dotnet @($hostDll, '--repeat', '0') -ExpectedExitCode 2 -TimeoutSeconds 30
    $null = Invoke-Step 'host-invalid-interval' $dotnet @($hostDll, '--repeat', '2', '--interval-ms', '-1') -ExpectedExitCode 2 -TimeoutSeconds 30
    $null = Invoke-Step 'host-interval-without-repeat' $dotnet @($hostDll, '--interval-ms', '10') -ExpectedExitCode 2 -TimeoutSeconds 30
    $autoStorageFailure = Invoke-Step 'host-auto-storage-failure' $dotnet @($hostDll, '--repeat', '3', '--interval-ms', '0', '--output', $blockedPath) -ExpectedExitCode 1 -TimeoutSeconds 30
    if ($autoStorageFailure.Stdout -notmatch 'State=Faulted Reason=RunFaulted StartedRuns=1 CompletedRuns=1' -or $autoStorageFailure.Stderr -notmatch 'Stage=Persist ComputedScore=100\.00') { throw 'Storage failure must stop auto after one run.' }
    foreach ($inspector in @('managed', 'native')) {
        $timeoutDir = Join-Path $outputDir "timeout-$inspector"
        $timedOut = Invoke-Step "host-timeout-$inspector" $dotnet @($hostDll, '--inspector', $inspector, '--timeout-ms', '0', '--output', $timeoutDir) -ExpectedExitCode 124 -TimeoutSeconds 30
        if ($timedOut.Stderr -notmatch 'RunId=[0-9a-fA-F-]{36} Status=TimedOut Stage=Prepare Reason=Timeout' -or $timedOut.Stdout -match 'Succeeded' -or (Test-Path -LiteralPath $timeoutDir)) {
            throw 'An immediate timeout must finish as TimedOut without acquiring or persisting a result.'
        }
        $autoTimeout = Invoke-Step "host-auto-timeout-$inspector" $dotnet @($hostDll, '--inspector', $inspector, '--repeat', '3', '--interval-ms', '0', '--timeout-ms', '0', '--output', $timeoutDir) -ExpectedExitCode 124 -TimeoutSeconds 30
        if ($autoTimeout.Stdout -notmatch 'State=Stopped Reason=RunTimedOut StartedRuns=1 CompletedRuns=1' -or $autoTimeout.Stderr -notmatch 'Status=TimedOut' -or (Test-Path -LiteralPath $timeoutDir)) { throw 'Timeout must stop auto without persisting a result.' }
    }
    $missingDllDir = Join-Path $outputDir 'missing native dll'
    New-Item -ItemType Directory -Path $missingDllDir | Out-Null
    Get-ChildItem -LiteralPath (Split-Path -Parent $hostDll) -File | Where-Object { $_.Name -ne 'NativeInspection.dll' } | Copy-Item -Destination $missingDllDir
    $missingDll = Invoke-Step 'host-missing-native-dll' $dotnet @((Join-Path $missingDllDir 'Inspection.Host.dll'), '--inspector', 'native', '--output', (Join-Path $missingDllDir 'results')) -ExpectedExitCode 1 -TimeoutSeconds 30
    if ($missingDll.Stderr -notmatch 'Status=Faulted Stage=Setup' -or $missingDll.Stdout -match 'Succeeded' -or (Test-Path -LiteralPath (Join-Path $missingDllDir 'results'))) {
        throw 'A missing native DLL must fail setup without falling back to the managed inspector or producing results.'
    }
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
