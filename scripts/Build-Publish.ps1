param(
    [string] $Configuration = "Release",
    [string] $OutputPath = "publish/pixlforge-web"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$clientRoot = Join-Path $repoRoot "src/pixlforge.client"
$apiRoot = Join-Path $repoRoot "src/PixlForge.Api"
$wwwroot = Join-Path $apiRoot "wwwroot"
$output = Join-Path $repoRoot $OutputPath

Push-Location $clientRoot
try {
    npm ci
    npm run build
}
finally {
    Pop-Location
}

if (Test-Path $wwwroot) {
    Remove-Item -LiteralPath $wwwroot -Recurse -Force
}

New-Item -ItemType Directory -Path $wwwroot | Out-Null
Copy-Item -Path (Join-Path $clientRoot "dist/*") -Destination $wwwroot -Recurse -Force

dotnet publish (Join-Path $apiRoot "PixlForge.Api.csproj") --configuration $Configuration --output $output
