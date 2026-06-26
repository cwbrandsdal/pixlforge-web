param(
    [int] $ApiPort = 7172,
    [int] $ClientPort = 5173
)

$ErrorActionPreference = "Stop"

Import-Module Microsoft.PowerShell.SecretManagement -ErrorAction Stop

function Set-SecretEnv {
    param(
        [Parameter(Mandatory)] [string] $SecretName,
        [Parameter(Mandatory)] [string] $EnvironmentName,
        [switch] $Optional
    )

    $secret = Get-Secret -Name $SecretName -AsPlainText -ErrorAction SilentlyContinue
    if ([string]::IsNullOrWhiteSpace($secret)) {
        if ($Optional) { return }
        throw "Missing SecretStore secret: $SecretName"
    }

    Set-Item -Path "Env:$EnvironmentName" -Value $secret
}

Set-SecretEnv -SecretName "WorkOS.PixlForge.ClientId" -EnvironmentName "WORKOS_CLIENT_ID"
Set-SecretEnv -SecretName "WorkOS.PixlForge.ApiKey" -EnvironmentName "WORKOS_API_KEY"
Set-SecretEnv -SecretName "WorkOS.PixlForge.ApiHostname" -EnvironmentName "WORKOS_API_HOSTNAME" -Optional
Set-SecretEnv -SecretName "PixlForge.Sql.ConnectionString" -EnvironmentName "PIXLFORGE_SQL_CONNECTION" -Optional

$env:ASPNETCORE_URLS = "https://localhost:$ApiPort"

Start-Process -WindowStyle Hidden -FilePath "dotnet" -ArgumentList @("run", "--project", "src/PixlForge.Api/PixlForge.Api.csproj")
Push-Location "src/pixlforge.client"
try {
    npm run dev -- --host 127.0.0.1 --port $ClientPort
}
finally {
    Pop-Location
}
