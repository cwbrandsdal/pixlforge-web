using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var workOs = WorkOsOptions.FromConfiguration(builder.Configuration);

builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
{
    options.AddPolicy("dev", policy =>
    {
        policy
            .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

if (workOs.IsConfigured)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "https://api.workos.com/",
                ValidateAudience = true,
                ValidAudience = workOs.ClientId,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeyResolver = WorkOsJwksCache.ResolveSigningKeys(workOs.JwksUri)
            };
        });

    builder.Services.AddAuthorization();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseCors("dev");
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseHttpsRedirection();

if (workOs.IsConfigured)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapGet("/api/auth/config", () => Results.Ok(new
{
    configured = workOs.IsConfigured,
    clientId = workOs.ClientId,
    apiHostname = workOs.ApiHostname
}));

app.MapGet("/api/health", () => Results.Ok(new
{
    app = "PixlForge",
    status = "ok",
    utc = DateTimeOffset.UtcNow
}));

if (workOs.IsConfigured)
{
    app.MapGet("/api/me", (ClaimsPrincipal user) => Results.Ok(new
    {
        subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"),
        email = user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email"),
        organizationId = user.FindFirstValue("org_id"),
        role = user.FindFirstValue(ClaimTypes.Role) ?? user.FindFirstValue("role"),
        permissions = user.FindAll("permissions").Select(claim => claim.Value).ToArray()
    })).RequireAuthorization();
}
else
{
    app.MapGet("/api/me", () => Results.Problem(
        title: "WorkOS is not configured.",
        detail: "Set WORKOS_CLIENT_ID and WORKOS_API_KEY before enabling authenticated API calls.",
        statusCode: StatusCodes.Status503ServiceUnavailable));
}

app.MapFallbackToFile("index.html");

app.Run();

sealed record WorkOsOptions(string ClientId, string ApiKey, string ApiHostname)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ApiKey);
    public string JwksUri => $"https://api.workos.com/sso/jwks/{ClientId}";

    public static WorkOsOptions FromConfiguration(IConfiguration configuration) =>
        new(
            configuration["WORKOS_CLIENT_ID"] ?? "",
            configuration["WORKOS_API_KEY"] ?? "",
            configuration["WORKOS_API_HOSTNAME"] ?? "api.workos.com");
}

static class WorkOsJwksCache
{
    private static readonly HttpClient HttpClient = new();
    private static readonly object Gate = new();
    private static IReadOnlyCollection<SecurityKey> keys = [];
    private static DateTimeOffset expiresAt = DateTimeOffset.MinValue;

    public static IssuerSigningKeyResolver ResolveSigningKeys(string jwksUri) =>
        (_, _, _, _) =>
        {
            lock (Gate)
            {
                if (DateTimeOffset.UtcNow < expiresAt && keys.Count > 0)
                {
                    return keys;
                }

                var jwks = HttpClient.GetStringAsync(jwksUri).GetAwaiter().GetResult();
                keys = new JsonWebKeySet(jwks).Keys.Cast<SecurityKey>().ToArray();
                expiresAt = DateTimeOffset.UtcNow.AddHours(6);
                return keys;
            }
        };
}
