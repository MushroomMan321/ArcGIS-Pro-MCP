param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "publish\ArcGisProMcpServer"
}

$projectPath = Join-Path $repoRoot "ArcGisProMcpServer\ArcGisProMcpServer.csproj"
dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained false `
    -p:PublishSingleFile=false `
    --output $OutputPath

$configSource = Join-Path $repoRoot "arcgis-pro-mcp.config.json"
if (Test-Path -LiteralPath $configSource) {
    Copy-Item -LiteralPath $configSource -Destination (Join-Path $OutputPath "arcgis-pro-mcp.config.json") -Force
}

$mcpExample = Join-Path $OutputPath "mcp-server.example.json"
$serverExe = Join-Path $OutputPath "ArcGisProMcpServer.exe"
$mcpJson = @{
    mcpServers = @{
        "arcgis-pro" = @{
            type = "stdio"
            command = $serverExe
            args = @()
            env = @{
                ARCGIS_PRO_MCP_CONFIG = (Join-Path $OutputPath "arcgis-pro-mcp.config.json")
            }
        }
    }
} | ConvertTo-Json -Depth 6

Set-Content -LiteralPath $mcpExample -Value $mcpJson -Encoding UTF8

Write-Host "Published MCP server: $serverExe"
Write-Host "Copied config: $(Join-Path $OutputPath "arcgis-pro-mcp.config.json")"
Write-Host "MCP client example: $mcpExample"
