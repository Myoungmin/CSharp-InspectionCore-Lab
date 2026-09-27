[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer / vswhere is required.' }
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $vsRoot) { throw 'Install the Visual Studio C++ x64 build tools.' }
$msbuild = Join-Path $vsRoot 'MSBuild/Current/Bin/MSBuild.exe'
$compiler = Join-Path $vsRoot 'VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64/cl.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'MSVC 14.44.35207 (v143) is required by NativeInspection.vcxproj.' }
$kits = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots'
if (-not (Test-Path -LiteralPath (Join-Path $kits.KitsRoot10 'Include/10.0.26100.0/ucrt'))) { throw 'Windows SDK 10.0.26100.0 is required.' }
& $msbuild (Join-Path $repoRoot 'native/NativeInspection/NativeInspection.vcxproj') /nologo /m /t:Build "/p:Configuration=$Configuration" /p:Platform=x64 /p:PreferredToolArchitecture=x64
if ($LASTEXITCODE -ne 0) { throw 'NativeInspection build failed.' }
$dll = Join-Path $repoRoot "artifacts/native/$Configuration/NativeInspection.dll"
if (-not (Test-Path -LiteralPath $dll)) { throw 'NativeInspection build produced no DLL.' }
Write-Host "NativeInspection: $dll"
Write-Host "SHA256: $((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash)"
