[CmdletBinding()]
param([switch]$Update)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$expected = @{
    'Inspection.Core' = @()
    'Inspection.Infrastructure' = @('Inspection.Core')
    'Inspection.Interop' = @('Inspection.Core')
    'Inspection.Contracts' = @()
    'Inspection.Client' = @('Inspection.Contracts')
    'Inspection.Host' = @('Inspection.Core', 'Inspection.Infrastructure', 'Inspection.Interop', 'Inspection.Contracts')
    'Inspection.Tests' = @('Inspection.Core')
    'Inspection.IntegrationTests' = @('Inspection.Core', 'Inspection.Infrastructure', 'Inspection.Interop', 'Inspection.Contracts', 'Inspection.Client', 'Inspection.Host')
}
$projects = @(Get-ChildItem (Join-Path $repoRoot 'src'), (Join-Path $repoRoot 'tests') -Filter '*.csproj' -Recurse | Sort-Object BaseName)
if ($projects.Count -ne $expected.Count) { throw 'Unexpected project count. Review the architecture policy.' }
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('@startuml')
$lines.Add('!pragma layout smetana')
$lines.Add('title Evaluated project references (Release / x64)')
$lines.Add('skinparam componentStyle rectangle')
foreach ($project in $projects) {
    if (-not $expected.ContainsKey($project.BaseName)) { throw "Unexpected project: $($project.BaseName)" }
    $lines.Add(('component "{0}" as {1}' -f $project.BaseName, $project.BaseName.Replace('.', '_')))
}

foreach ($project in $projects) {
    $raw = & dotnet msbuild $project.FullName -nologo -property:Configuration=Release -getItem:ProjectReference,PackageReference -getProperty:TargetFramework,PlatformTarget
    if ($LASTEXITCODE -ne 0) { throw "MSBuild evaluation failed: $($project.Name)" }
    $evaluated = ($raw -join "`n") | ConvertFrom-Json
    if ($evaluated.Properties.TargetFramework -ne 'net9.0' -or $evaluated.Properties.PlatformTarget -ne 'x64') {
        throw "Unexpected framework/platform: $($project.Name)"
    }
    $references = @($evaluated.Items.ProjectReference | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.FullPath) } | Sort-Object)
    if (($references -join ',') -ne (($expected[$project.BaseName] | Sort-Object) -join ',')) {
        throw "Disallowed or missing project reference: $($project.Name) -> $($references -join ', ')"
    }
    foreach ($reference in $evaluated.Items.ProjectReference) {
        $target = $projects | Where-Object { $_.FullName -eq $reference.FullPath }
        if (-not $target) { throw "Reference outside the known project set: $($reference.FullPath)" }
    }
    if ($project.BaseName -notin @('Inspection.Tests', 'Inspection.IntegrationTests') -and @($evaluated.Items.PackageReference).Count -gt 0) {
        throw "Production projects must depend only on BCL and the approved project references: $($project.Name)"
    }
    foreach ($reference in $references) {
        $lines.Add(('{0} --> {1}' -f $project.BaseName.Replace('.', '_'), $reference.Replace('.', '_')))
    }
}
$nativeProject = Join-Path $repoRoot 'native/NativeInspection/NativeInspection.vcxproj'
[xml]$native = Get-Content -LiteralPath $nativeProject -Raw -Encoding UTF8
if (@($native.SelectNodes('//*[local-name()="ProjectReference"]')).Count -ne 0) { throw 'NativeInspection must not reference managed projects.' }
$lines.Add('component "NativeInspection (C++ DLL)" as NativeInspection')
$lines.Add('Inspection_Interop ..> NativeInspection : C ABI at runtime; no managed ProjectReference')
$lines.Add('note right of Inspection_Core')
$lines.Add('Core declares ports.')
$lines.Add('No adapter references.')
$lines.Add('end note')
$lines.Add('@enduml')
$generated = ($lines -join "`n") + "`n"
$path = Join-Path $repoRoot 'docs/diagrams/dependencies.generated.puml'
if ($Update) {
    [IO.File]::WriteAllText($path, $generated, [Text.UTF8Encoding]::new($false))
}
elseif (-not (Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path).Replace("`r`n", "`n") -cne $generated) {
    throw 'Dependency diagram is stale. Run scripts/check-architecture.ps1 -Update.'
}
Write-Host 'Architecture: eight managed projects and one native project; reference rules and framework/platform passed.'
