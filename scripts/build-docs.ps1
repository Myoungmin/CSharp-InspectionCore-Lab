[CmdletBinding()]
param([switch]$Update)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'check-required-tests.ps1')
$adrDir = Join-Path $repoRoot 'docs/adr'
$adrIndex = [IO.File]::ReadAllText((Join-Path $adrDir 'README.md'))
if (-not (Test-Path -LiteralPath (Join-Path $adrDir 'template.md') -PathType Leaf)) { throw 'ADR template is missing.' }
$adrFiles = @(Get-ChildItem -LiteralPath $adrDir -Filter '*.md' -File | Where-Object { $_.Name -notin @('README.md', 'template.md') } | Sort-Object Name)
if ($adrFiles.Count -eq 0) { throw 'At least one architecture decision record is required.' }
$adrIds = @()
foreach ($file in $adrFiles) {
    if ($file.Name -cnotmatch '^([0-9]{4})-[a-z0-9]+(?:-[a-z0-9]+)*\.md$') { throw "Invalid ADR filename: $($file.Name)" }
    $id = $Matches[1]
    if ($id -eq '0000' -or $id -in $adrIds) { throw "Invalid or duplicate ADR number: $id" }
    $adrIds += $id
    $content = [IO.File]::ReadAllText($file.FullName)
    if ($content -notmatch ('\A# ADR-' + $id + ': [^\r\n]+\r?\n')) { throw "ADR title must match its filename: $($file.Name)" }
    $statusMatch = [regex]::Matches($content, '(?m)^- Status: (Proposed|Accepted|Rejected|Superseded)\r?$')
    if ($statusMatch.Count -ne 1) { throw "ADR requires one valid status: $($file.Name)" }
    $status = $statusMatch[0].Groups[1].Value
    $dateMatch = [regex]::Matches($content, '(?m)^- Date: ([0-9]{4}-[0-9]{2}-[0-9]{2})\r?$')
    $recordedDate = [DateTime]::MinValue
    if ($dateMatch.Count -ne 1 -or -not [DateTime]::TryParseExact($dateMatch[0].Groups[1].Value, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$recordedDate)) {
        throw "ADR requires one valid recording date: $($file.Name)"
    }
    foreach ($section in @('Context', 'Decision', 'Alternatives', 'Consequences', 'Validation', 'Links')) {
        $sectionMatch = [regex]::Matches($content, ('(?ms)^## ' + $section + '\r?\n(.*?)(?=^## |\z)'))
        if ($sectionMatch.Count -ne 1 -or [string]::IsNullOrWhiteSpace($sectionMatch[0].Groups[1].Value)) { throw "ADR requires a nonempty $section section: $($file.Name)" }
    }
    $indexRows = [regex]::Matches($adrIndex, ('(?m)^\| \[[^\]\r\n]+\]\(' + [regex]::Escape($file.Name) + '\) \| ([A-Za-z]+) \|[^\r\n]*$'))
    if ($indexRows.Count -ne 1 -or $indexRows[0].Groups[1].Value -cne $status) { throw "ADR index entry/status mismatch: $($file.Name)" }
}
Write-Host "ADR: $($adrFiles.Count) records, unique IDs, metadata, sections and index passed."
$diagramDir = Join-Path $repoRoot 'docs/diagrams'
$committedDir = Join-Path $diagramDir 'generated'
$toolchain = Get-Content (Join-Path $repoRoot 'docs/toolchain.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($toolchain.layoutEngine -ne 'smetana') { throw 'Documentation uses the pinned Smetana layout engine.' }
if (-not $env:PLANTUML_JAR -or -not (Test-Path -LiteralPath $env:PLANTUML_JAR)) {
    throw "Set PLANTUML_JAR to PlantUML $($toolchain.plantUmlVersion). See README.md."
}
if ((Get-FileHash -LiteralPath $env:PLANTUML_JAR -Algorithm SHA256).Hash -ne $toolchain.plantUmlSha256) {
    throw 'PlantUML JAR differs from docs/toolchain.json. Tool upgrades require regeneration and review.'
}
$java = (Get-Command java -ErrorAction Stop).Source
$outputDir = Join-Path $repoRoot ('artifacts/docs/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath $diagramDir -Filter '*.puml' -File | Sort-Object Name)
$requiredDiagrams = @('architecture', 'auto-sequence', 'cancel-shutdown-sequence', 'core-class', 'cpp-cli-comparison', 'dependencies.generated', 'ipc-sequence', 'native-interop', 'run-sequence', 'run-state', 'storage-diagnostics')
if (($sources.BaseName -join ',') -ne (($requiredDiagrams | Sort-Object) -join ',')) { throw 'M6 requires the eleven documented diagrams. Review the inventory when adding milestones.' }
foreach ($source in $sources) {
    & $java '-Dfile.encoding=UTF-8' -jar $env:PLANTUML_JAR -charset UTF-8 -failfast2 -checkonly $source.FullName
    if ($LASTEXITCODE -ne 0) { throw "PlantUML syntax failed: $($source.Name)" }
    & $java '-Dfile.encoding=UTF-8' -jar $env:PLANTUML_JAR -charset UTF-8 '-Playout=smetana' -nometadata -tsvg -o $outputDir $source.FullName
    if ($LASTEXITCODE -ne 0) { throw "PlantUML render failed: $($source.Name)" }
    $svg = Join-Path $outputDir ($source.BaseName + '.svg')
    if (-not (Test-Path -LiteralPath $svg)) { throw "SVG was not generated: $svg" }
    [xml]$xml = [IO.File]::ReadAllText($svg)
    if ($xml.DocumentElement.LocalName -ne 'svg') { throw "Invalid SVG: $svg" }
}

if ($Update) { New-Item -ItemType Directory -Path $committedDir -Force | Out-Null }
$expectedNames = @($sources | ForEach-Object { $_.BaseName + '.svg' })
$stale = @(Get-ChildItem -LiteralPath $committedDir -Filter '*.svg' -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin $expectedNames })
if ($stale.Count -gt 0) { throw "Obsolete SVG files require removal: $($stale.Name -join ', ')" }
foreach ($name in $expectedNames) {
    $fresh = Join-Path $outputDir $name
    $committed = Join-Path $committedDir $name
    if ($Update) { Copy-Item -LiteralPath $fresh -Destination $committed -Force }
    elseif (-not (Test-Path -LiteralPath $committed) -or (Get-FileHash -LiteralPath $fresh).Hash -ne (Get-FileHash -LiteralPath $committed).Hash) {
        throw "Stale SVG: $name. Run scripts/build-docs.ps1 -Update."
    }
}

$markdownFiles = @(Get-ChildItem -LiteralPath $repoRoot -Filter '*.md' -File)
$markdownFiles += @(Get-ChildItem (Join-Path $repoRoot 'docs'), (Join-Path $repoRoot 'tasks') -Filter '*.md' -File -Recurse)
foreach ($file in $markdownFiles) {
    $content = [IO.File]::ReadAllText($file.FullName)
    foreach ($match in [regex]::Matches($content, '!?(?:\[[^\]]*\])\(([^)]+)\)')) {
        $link = $match.Groups[1].Value.Trim('<', '>')
        if ($link -match '^(https?://|mailto:|#)') { continue }
        $target = ($link -split '#')[0]
        if (-not (Test-Path -LiteralPath (Join-Path $file.DirectoryName $target))) {
            throw "Broken relative link in $($file.Name): $link"
        }
    }
}

$coreText = (Get-ChildItem (Join-Path $repoRoot 'src/Inspection.Core') -Filter '*.cs' -File | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
$classDiagram = [IO.File]::ReadAllText((Join-Path $diagramDir 'core-class.puml'))
foreach ($match in [regex]::Matches($classDiagram, '(?m)^\s*(?:class|interface|enum)\s+(\w+)')) {
    $name = $match.Groups[1].Value
    if ($coreText -notmatch ('\b(?:class|record|interface|enum)\s+' + [regex]::Escape($name) + '\b')) {
        throw "Core type in diagram is missing: $name"
    }
}
Write-Host 'Documentation: ADRs, syntax, eleven SVGs, relative links and named Core types passed.'
