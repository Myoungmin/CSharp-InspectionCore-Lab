[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build-native.ps1') -Configuration $Configuration
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.CLI.Support -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $vsRoot) { throw 'Install the Visual Studio v143 C++/CLI support component.' }
$msbuild = Join-Path $vsRoot 'MSBuild/Current/Bin/MSBuild.exe'
$interop = Join-Path $repoRoot 'src/Inspection.Interop/Inspection.Interop.csproj'
& dotnet restore $interop --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Interop locked restore failed.' }
& dotnet build $interop -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Managed lifetime build failed.' }
& $msbuild (Join-Path $repoRoot 'src/Inspection.CppCli/Inspection.CppCli.vcxproj') /nologo /m /t:Build "/p:Configuration=$Configuration" /p:Platform=x64 /p:BuildProjectReferences=false /p:PreferredToolArchitecture=x64
if ($LASTEXITCODE -ne 0) { throw 'Inspection.CppCli build failed.' }
foreach ($name in @('Inspection.CppCli.dll', 'ijwhost.dll')) {
    $path = Join-Path $repoRoot "artifacts/cppcli/$Configuration/$name"
    if (-not (Test-Path -LiteralPath $path)) { throw "C++/CLI build produced no $name" }
    Write-Host "$name SHA256: $((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)"
}
