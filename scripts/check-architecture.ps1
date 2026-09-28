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
$lines.Add('component "Inspection.CppCli (mixed DLL)" as Inspection_CppCli')
foreach ($project in $projects) {
    if (-not $expected.ContainsKey($project.BaseName)) { throw "Unexpected project: $($project.BaseName)" }
    $lines.Add(('component "{0}" as {1}' -f $project.BaseName, $project.BaseName.Replace('.', '_')))
}

foreach ($project in $projects) {
    $raw = & dotnet msbuild $project.FullName -nologo -property:Configuration=Release -getItem:ProjectReference,PackageReference,Reference -getProperty:TargetFramework,PlatformTarget,NuGetPackageRoot
    if ($LASTEXITCODE -ne 0) { throw "MSBuild evaluation failed: $($project.Name)" }
    $evaluated = ($raw -join "`n") | ConvertFrom-Json
    $files = @($evaluated.Items.Reference)
    if ($project.BaseName -in @('Inspection.Tests', 'Inspection.IntegrationTests')) {
        $framework = @($files | Where-Object { $_.Identity -ceq 'Microsoft.VisualStudio.TestPlatform.TestFramework.Extensions' })
        $frameworkPath = Join-Path $evaluated.Properties.NuGetPackageRoot 'mstest.testframework/3.6.4/build/net8.0/Microsoft.VisualStudio.TestPlatform.TestFramework.Extensions.dll'
        if ($framework.Count -ne 1 -or [IO.Path]::GetFullPath($framework[0].HintPath) -ne [IO.Path]::GetFullPath($frameworkPath)) { throw 'Unexpected MSTest framework extension reference.' }
        $files = @($files | Where-Object { $_.Identity -cne 'Microsoft.VisualStudio.TestPlatform.TestFramework.Extensions' })
    }
    if ($project.BaseName -in @('Inspection.Host', 'Inspection.IntegrationTests')) {
        $cliPath = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/cppcli/Release/Inspection.CppCli.dll'))
        if ($files.Count -ne 1 -or $files[0].Identity -cne 'Inspection.CppCli' -or
            [IO.Path]::GetFullPath($files[0].HintPath) -ne $cliPath -or $files[0].Private -ne 'true') {
            throw "Only the built Inspection.CppCli file reference is allowed: $($project.Name)"
        }
        $lines.Add(('{0} --> Inspection_CppCli : assembly reference; built first' -f $project.BaseName.Replace('.', '_')))
    }
    elseif ($files.Count -ne 0) { throw "Unexpected assembly reference: $($project.Name)" }
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
    if ($project.BaseName -eq 'Inspection.Infrastructure') {
        $packages = @($evaluated.Items.PackageReference)
        if ($packages.Count -ne 1 -or $packages[0].Identity -cne 'Microsoft.Data.Sqlite' -or $packages[0].Version -cne '9.0.20') {
            throw 'Infrastructure requires only the pinned Microsoft.Data.Sqlite 9.0.20 direct package.'
        }
    }
    elseif ($project.BaseName -notin @('Inspection.Tests', 'Inspection.IntegrationTests') -and @($evaluated.Items.PackageReference).Count -gt 0) {
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
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.CLI.Support -property installationPath
if (-not $vsRoot) { throw 'C++/CLI support is required.' }
$msbuild = Join-Path $vsRoot 'MSBuild/Current/Bin/MSBuild.exe'
$cliProject = Join-Path $repoRoot 'src/Inspection.CppCli/Inspection.CppCli.vcxproj'
$raw = & $msbuild $cliProject -nologo -property:Configuration=Release -property:Platform=x64 -getItem:ProjectReference,Reference,PackageReference -getProperty:TargetFramework,Platform,CLRSupport,VCToolsVersion,WindowsTargetPlatformVersion
if ($LASTEXITCODE -ne 0) { throw 'C++/CLI MSBuild evaluation failed.' }
$cli = ($raw -join "`n") | ConvertFrom-Json
if ($cli.Properties.TargetFramework -ne 'net9.0' -or $cli.Properties.Platform -ne 'x64' -or $cli.Properties.CLRSupport -ne 'NetCore' -or
    $cli.Properties.VCToolsVersion -ne '14.44.35207' -or $cli.Properties.WindowsTargetPlatformVersion -ne '10.0.26100.0') { throw 'C++/CLI toolchain drift.' }
$cliReferences = @($cli.Items.ProjectReference | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.FullPath) } | Sort-Object)
if (($cliReferences -join ',') -cne 'Inspection.Core,Inspection.Interop' -or @($cli.Items.Reference).Count -ne 0 -or @($cli.Items.PackageReference).Count -ne 0) { throw 'Unexpected C++/CLI dependency.' }
foreach ($reference in $cli.Items.ProjectReference) {
    $target = $projects | Where-Object { $_.FullName -eq $reference.FullPath }
    if (-not $target) { throw 'C++/CLI reference outside the known project set.' }
    $lines.Add(('Inspection_CppCli --> {0}' -f $target.BaseName.Replace('.', '_')))
}
$lines.Add('Inspection_CppCli ..> NativeInspection : C++ import library / C ABI')
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
Write-Host 'Architecture: eight C# projects, one C++/CLI and one native project; reference rules and framework/platform passed.'
