# PixlForge Web

PixlForge Web is the ASP.NET Core and React version of PixlForge, secured with WorkOS AuthKit.

## Structure

- `src/PixlForge.Api` - .NET backend, static host, JWT-protected API endpoints.
- `src/pixlforge.client` - React frontend using `@workos-inc/authkit-react`.
- `scripts` - local build, SecretStore, and Coruscant publish helpers.

## Secrets

The app expects runtime environment variables. On this Windows machine, use SecretStore names:

- `WorkOS.PixlForge.ClientId`
- `WorkOS.PixlForge.ApiKey`
- `WorkOS.PixlForge.ApiHostname` optional, defaults to `api.workos.com`
- `PixlForge.Sql.ConnectionString` optional, enables SQL Server persistence for user state and saved OpenAI API keys.

The local helper reads those names and sets process-only environment variables:

```powershell
.\scripts\Start-Dev.ps1
```

## Development

```powershell
dotnet restore
Push-Location src\pixlforge.client
npm install
Pop-Location
.\scripts\Start-Dev.ps1
```

The frontend runs on `http://localhost:5173` and proxies API calls to the .NET backend.

## Publish Build

```powershell
.\scripts\Build-Publish.ps1
```

Output is written to `publish\pixlforge-web`. Deploying that output to Coruscant and writing GoDaddy DNS are guarded infrastructure operations.

## Persistence

PixlForge stores generated image/reference files under `PIXLFORGE_DATA_ROOT`. Production defaults to `/var/lib/pixlforge-web`, outside the deploy directory, so files survive app publishes.

When `PIXLFORGE_SQL_CONNECTION` is set, user settings, projects, generation history, and saved OpenAI API keys are persisted in SQL Server. File storage remains on `PIXLFORGE_DATA_ROOT` for image and reference binaries. If SQL is not configured, the app falls back to the local JSON file store under the data root.
