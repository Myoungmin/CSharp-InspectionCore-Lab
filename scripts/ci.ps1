[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$evidenceDir = Join-Path $repoRoot 'artifacts/ci'
New-Item -ItemType Directory -Path $evidenceDir -Force | Out-Null
. (Join-Path $PSScriptRoot 'process.ps1')
$powershell = Join-Path $PSHOME 'powershell.exe'
$report = [ordered]@{ status = 'Failed'; stage = 'Preflight'; exitCode = $null; error = $null }
$failure = $null
try {
    $preflight = Invoke-RecordedProcess -FilePath $powershell -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'prepare-ci.ps1')) -WorkingDirectory $repoRoot -LogPrefix (Join-Path $evidenceDir 'preflight') -TimeoutSeconds 120
    $report.exitCode = $preflight.ExitCode
    if ($preflight.ExitCode -ne 0) { throw 'CI tool preflight failed. See artifacts/ci/preflight.*.log.' }
    $paths = Get-Content -LiteralPath (Join-Path $evidenceDir 'tool-paths.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $env:PATH = $paths.javaDirectory + [IO.Path]::PathSeparator + $env:PATH
    $env:PLANTUML_JAR = $paths.plantUmlJar
    $report.stage = 'Verification'
    $report.exitCode = $null
    $verification = Invoke-RecordedProcess -FilePath $powershell -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'verify.ps1')) -WorkingDirectory $repoRoot -LogPrefix (Join-Path $evidenceDir 'verify') -TimeoutSeconds 1200
    Write-Host $verification.Stdout
    $report.exitCode = $verification.ExitCode
    if ($verification.ExitCode -ne 0) { throw 'Full verification failed. See artifacts/ci/verify.*.log and artifacts/verification.' }
    $report.status = 'Passed'
}
catch { $failure = $_; $report.error = $_.Exception.Message }
finally {
    [IO.File]::WriteAllText((Join-Path $evidenceDir 'result.json'), ($report | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Write-Host "CI $($report.status) at $($report.stage). Evidence: artifacts/ci"
}
if ($null -ne $failure) { Write-Error -ErrorRecord $failure -ErrorAction Continue; exit 1 }
