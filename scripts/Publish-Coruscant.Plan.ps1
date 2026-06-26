param(
    [string] $Hostname = "pixlforge.ai",
    [int] $BackendPort = 5128,
    [string] $RemoteAppPath = "/srv/apps/pixlforge-web",
    [string] $RemoteDataPath = "/var/lib/pixlforge-web",
    [string] $SqlServer = "192.168.1.128,1433",
    [string] $SqlDatabase = "PixlForge"
)

$ErrorActionPreference = "Stop"

@"
Planned guarded operations for $Hostname:

1. Build local publish output:
   .\scripts\Build-Publish.ps1

2. Copy publish output to Coruscant:
   scp -i `$env:USERPROFILE\.ssh\coruscant_agent_ed25519 -r publish/pixlforge-web/* agent-remote@192.168.2.12:$RemoteAppPath/

3. Configure persistent storage on Coruscant:
   $RemoteDataPath owned by the service account.
   PIXLFORGE_DATA_ROOT=$RemoteDataPath in /etc/pixlforge-web/pixlforge-web.env.

4. Configure SQL Server persistence when approved:
   SQL Server: $SqlServer
   Database: $SqlDatabase
   SecretStore/env value: PIXLFORGE_SQL_CONNECTION.

5. Configure an ASP.NET systemd service on Coruscant for port $BackendPort.

6. Configure Caddy on Coruscant for:
   $Hostname -> http://127.0.0.1:$BackendPort

7. Define GoDaddy DNS:
   zone pixlforge.ai
   A @ 193.215.242.28
   A www 193.215.242.28

These steps change public infrastructure and should be executed only after explicit approval.
"@
