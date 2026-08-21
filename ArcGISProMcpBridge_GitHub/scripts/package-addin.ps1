param(
    [string]$Configuration = "Debug",
    [switch]$ForceConfig
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$addinProject = Join-Path $repoRoot "ArcGisProBridgeAddIn"
$packageName = "ArcGisProBridgeAddIn"

# ArcGIS Pro installs per-machine (HKLM) or per-user (HKCU, under %LOCALAPPDATA%),
# so resolve the install directory rather than assuming C:\Program Files.
$proInstallDir = $null
foreach ($key in @('HKCU:\SOFTWARE\ESRI\ArcGISPro', 'HKLM:\SOFTWARE\ESRI\ArcGISPro')) {
    if (Test-Path $key) {
        $candidate = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallDir
        if ($candidate) { $proInstallDir = $candidate; break }
    }
}
if (-not $proInstallDir) {
    foreach ($candidate in @("$env:LOCALAPPDATA\Programs\ArcGIS\Pro\", "$env:ProgramFiles\ArcGIS\Pro\")) {
        if (Test-Path (Join-Path $candidate "bin\RegisterAddIn.exe")) { $proInstallDir = $candidate; break }
    }
}
if (-not $proInstallDir) {
    throw "Could not locate an ArcGIS Pro installation."
}

$registerAddIn = Join-Path $proInstallDir "bin\RegisterAddIn.exe"

dotnet build (Join-Path $repoRoot "ArcGISProMcpBridge.sln") --configuration $Configuration

if (-not (Test-Path -LiteralPath $registerAddIn)) {
    throw "RegisterAddIn.exe was not found at $registerAddIn"
}

# The add-in target framework tracks the .NET runtime of the installed ArcGIS Pro
# (3.5/3.6 => net8.0, 3.7 => net10.0), so discover the build output rather than assume it.
# Key off the freshest built assembly: framework directory names do not sort meaningfully
# (net8.0-windows8.0 sorts above net10.0-windows), and stale directories linger after a
# retarget, so a name sort can select an empty leftover.
$outDir = Get-ChildItem -Path (Join-Path $addinProject "bin\$Configuration") -Directory -Filter "net*-windows*" |
    ForEach-Object { Get-Item (Join-Path $_.FullName "$packageName.dll") -ErrorAction SilentlyContinue } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1 -ExpandProperty DirectoryName
if (-not $outDir) {
    throw "No built $packageName.dll found under $(Join-Path $addinProject "bin\$Configuration"). Build the solution first."
}

Write-Host "Using add-in build output: $outDir"

$packagePath = Join-Path $outDir "$packageName.esriAddinX"
$zipPath = Join-Path $outDir "$packageName.zip"

$stage = Join-Path $env:TEMP ("ArcGisProBridgeAddInPackage_" + [guid]::NewGuid().ToString("n"))
$install = Join-Path $stage "Install"
New-Item -ItemType Directory -Force -Path $install | Out-Null

Copy-Item -LiteralPath (Join-Path $addinProject "Config.daml") -Destination $stage
Get-ChildItem -Path $outDir -File |
    Where-Object { $_.Extension -notin ".zip", ".esriAddinX" } |
    Copy-Item -Destination $install

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -Force
Move-Item -LiteralPath $zipPath -Destination $packagePath -Force

& $registerAddIn $packagePath /s

$configSource = Join-Path $repoRoot "arcgis-pro-mcp.config.json"
$configDir = Join-Path $env:LOCALAPPDATA "ArcGISProMcpBridge"
$configTarget = Join-Path $configDir "config.json"
if (Test-Path -LiteralPath $configSource) {
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null
    if ($ForceConfig -or -not (Test-Path -LiteralPath $configTarget)) {
        Copy-Item -LiteralPath $configSource -Destination $configTarget -Force
        Write-Host "Installed default config: $configTarget"
    }
    else {
        Write-Host "Existing config preserved: $configTarget"
    }
}

Write-Host "Packaged and registered: $packagePath"
Write-Host "Restart ArcGIS Pro before testing pro.health."
