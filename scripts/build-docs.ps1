[CmdletBinding()]
param([switch]$Update)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$diagramDir = Join-Path $repoRoot 'docs/diagrams'
$committedDir = Join-Path $diagramDir 'generated'
$toolchain = Get-Content (Join-Path $repoRoot 'docs/toolchain.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($toolchain.layoutEngine -ne 'smetana') { throw 'M1 documentation uses the pinned Smetana layout engine.' }
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
if ($sources.Count -ne 4) { throw 'M1 requires four diagrams. Review the diagram inventory when adding milestones.' }
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
Write-Host 'Documentation: syntax, four SVGs, relative links and named Core types passed.'
