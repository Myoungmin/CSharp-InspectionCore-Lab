[CmdletBinding()]
param([string]$CoreTrx, [string]$IntegrationTrx)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'docs/verification-map.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or @($manifest.requirements).Count -eq 0) { throw 'Invalid required-test manifest.' }
if ([bool]$CoreTrx -xor [bool]$IntegrationTrx) { throw 'Supply both CoreTrx and IntegrationTrx, or neither for metadata validation.' }
$ids = @()
$requiredKeys = @()
foreach ($entry in $manifest.requirements) {
    if ($entry.id -notmatch '^[A-Z0-9]+-[0-9]{3}$' -or $entry.id -in $ids) { throw "Invalid or duplicate requirement ID: $($entry.id)" }
    $ids += $entry.id
    if ($entry.suite -notin @('core', 'integration') -or [string]::IsNullOrWhiteSpace($entry.className) -or [string]::IsNullOrWhiteSpace($entry.method) -or @($entry.cases).Count -eq 0) { throw "Invalid test requirement: $($entry.id)" }
    foreach ($path in @($entry.source, $entry.adr)) {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $path) -PathType Leaf)) { throw "Missing requirement reference: $path" }
    }
    foreach ($case in $entry.cases) {
        $key = "$($entry.suite)|$($entry.className)|$($entry.method)|$case"
        if ([string]::IsNullOrWhiteSpace($case) -or $key -in $requiredKeys) { throw "Invalid or duplicate required case: $key" }
        $requiredKeys += $key
    }
}
if (-not $CoreTrx) {
    Write-Host "Required-test metadata: $($ids.Count) requirements, $($requiredKeys.Count) cases passed."
    return
}
$actual = @{}
foreach ($suite in @(@{ Name = 'core'; Path = $CoreTrx }, @{ Name = 'integration'; Path = $IntegrationTrx })) {
    [xml]$trx = [IO.File]::ReadAllText($suite.Path)
    $definitions = @{}
    foreach ($definition in $trx.TestRun.TestDefinitions.UnitTest) { $definitions[$definition.id] = $definition.TestMethod }
    foreach ($result in $trx.TestRun.Results.UnitTestResult) {
        $method = $definitions[$result.testId]
        if (-not $method) { throw "Missing TRX definition: $($result.testId)" }
        $key = "$($suite.Name)|$($method.className)|$($method.name)|$($result.testName)"
        if ($actual.ContainsKey($key)) { throw "Duplicate executed case: $key" }
        $actual[$key] = $result.outcome
    }
}
foreach ($key in $requiredKeys) {
    if (-not $actual.ContainsKey($key) -or $actual[$key] -cne 'Passed') { throw "Required case missing or not passed: $key" }
}
Write-Host "Required tests: all $($requiredKeys.Count) exact cases passed."
