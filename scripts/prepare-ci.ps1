[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$evidenceDir = Join-Path $repoRoot 'artifacts/ci'
New-Item -ItemType Directory -Path $evidenceDir -Force | Out-Null
. (Join-Path $PSScriptRoot 'process.ps1')
$report = [ordered]@{ status = 'Failed'; sdk = $null; visualStudio = $null; compiler = $null; windowsSdk = '10.0.26100.0'; java = $null; plantUmlSha256 = $null; error = $null }
try {
    if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess) { throw 'CI requires Windows x64 PowerShell.' }
    $sdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -cne '9.0.305') { throw 'CI requires the pinned .NET SDK 9.0.305.' }
    $report.sdk = $sdk
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.VisualStudio.Component.VC.CLI.Support -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $vsRoot) { throw 'Visual Studio with C++ x64 and C++/CLI support is required.' }
    $report.visualStudio = $vsRoot
    $compiler = Join-Path $vsRoot 'VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64/cl.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw 'Pinned MSVC 14.44.35207 is missing; do not substitute another toolset.' }
    $report.compiler = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
    $kits = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots'
    if (-not (Test-Path -LiteralPath (Join-Path $kits.KitsRoot10 'Include/10.0.26100.0/ucrt'))) { throw 'Windows SDK 10.0.26100.0 is missing.' }

    $java = if ($env:JAVA_HOME_11_X64) { Join-Path $env:JAVA_HOME_11_X64 'bin/java.exe' } else { (Get-Command java -ErrorAction Stop).Source }
    $javaVersion = Invoke-RecordedProcess -FilePath $java -Arguments @('-version') -WorkingDirectory $repoRoot -LogPrefix (Join-Path $evidenceDir 'java-version') -TimeoutSeconds 30
    if ($javaVersion.ExitCode -ne 0 -or ($javaVersion.Stdout + $javaVersion.Stderr) -notmatch 'version "11\.') { throw 'Java 11 is required for the pinned diagrams.' }
    $report.java = $java
    $toolsDir = Join-Path $repoRoot 'artifacts/ci-tools'
    New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
    $toolchain = Get-Content -LiteralPath (Join-Path $repoRoot 'docs/toolchain.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $jar = Join-Path $toolsDir 'plantuml.jar'
    if (-not (Test-Path -LiteralPath $jar)) {
        $version = $toolchain.plantUmlVersion
        $url = "https://repo.maven.apache.org/maven2/net/sourceforge/plantuml/plantuml/$version/plantuml-$version.jar"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $jar -TimeoutSec 90
    }
    $report.plantUmlSha256 = (Get-FileHash -LiteralPath $jar -Algorithm SHA256).Hash
    if ($report.plantUmlSha256 -cne $toolchain.plantUmlSha256) { throw 'PlantUML SHA256 differs from the repository pin.' }
    $report.status = 'Passed'
    [IO.File]::WriteAllText((Join-Path $evidenceDir 'tool-paths.json'),
        (@{ javaDirectory = (Split-Path -Parent $java); plantUmlJar = $jar } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Write-Host 'CI tool preflight passed.'
}
catch { $report.error = $_.Exception.Message; throw }
finally {
    [IO.File]::WriteAllText((Join-Path $evidenceDir 'environment.json'), ($report | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
