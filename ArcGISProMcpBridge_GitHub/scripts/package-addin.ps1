param(
    [string]$Configuration = "Debug",
    [switch]$ForceConfig
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$addinProject = Join-Path $repoRoot "ArcGisProBridgeAddIn"
$outDir = Join-Path $addinProject "bin\$Configuration\net8.0-windows8.0"
$packagePath = Join-Path $outDir "ArcGisProBridgeAddIn.esriAddinX"
$zipPath = Join-Path $outDir "ArcGisProBridgeAddIn.zip"
$registerAddIn = "C:\Program Files\ArcGIS\Pro\bin\RegisterAddIn.exe"

dotnet build (Join-Path $repoRoot "ArcGISProMcpBridge.sln") --configuration $Configuration

if (-not (Test-Path -LiteralPath $registerAddIn)) {
    throw "RegisterAddIn.exe was not found at $registerAddIn"
}

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
