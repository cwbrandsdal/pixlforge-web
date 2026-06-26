using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using SkiaSharp;

var builder = WebApplication.CreateBuilder(args);

var workOs = WorkOsOptions.FromConfiguration(builder.Configuration);
var dataRoot = builder.Configuration["PIXLFORGE_DATA_ROOT"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(new PixlForgeStore(dataRoot));
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
                ValidIssuers = ["https://api.workos.com/", "https://api.workos.com"],
                ValidateAudience = false,
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

var api = app.MapGroup("/api");
if (workOs.IsConfigured)
{
    api.RequireAuthorization();
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

api.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(UserProfile.From(user)));
api.MapGet("/state", async (ClaimsPrincipal user, PixlForgeStore store) => Results.Ok(await store.Load(UserKey(user))));
api.MapPost("/settings", async (ClaimsPrincipal user, PixlForgeStore store, PixlForgeSettings settings) =>
    Results.Ok(await store.UpdateSettings(UserKey(user), settings)));
api.MapPost("/projects", async (ClaimsPrincipal user, PixlForgeStore store, CreateProjectRequest request) =>
    Results.Ok(await store.CreateProject(UserKey(user), request.Name)));
api.MapPost("/projects/{projectId}/activate", async (ClaimsPrincipal user, PixlForgeStore store, string projectId) =>
    Results.Ok(await store.SetActiveProject(UserKey(user), projectId)));
api.MapDelete("/projects/{projectId}", async (ClaimsPrincipal user, PixlForgeStore store, string projectId) =>
    Results.Ok(await store.DeleteProject(UserKey(user), projectId)));
api.MapDelete("/references/{projectId}/{referenceId}", async (ClaimsPrincipal user, PixlForgeStore store, string projectId, string referenceId) =>
    Results.Ok(await store.RemoveReference(UserKey(user), projectId, referenceId)));
api.MapGet("/secrets/openai", async (ClaimsPrincipal user, PixlForgeStore store) =>
    Results.Ok(await store.GetSecretStatus(UserKey(user))));
api.MapPost("/secrets/openai", async (ClaimsPrincipal user, PixlForgeStore store, SaveOpenAiKeyRequest request) =>
    Results.Ok(await store.SaveOpenAiApiKey(UserKey(user), request.ApiKey)));
api.MapDelete("/secrets/openai", async (ClaimsPrincipal user, PixlForgeStore store) =>
    Results.Ok(await store.ClearOpenAiApiKey(UserKey(user))));

api.MapPost("/references/{projectId}", async (
    ClaimsPrincipal user,
    PixlForgeStore store,
    string projectId,
    [FromForm] IFormFileCollection files) =>
{
    var state = await store.AddReferenceFiles(UserKey(user), projectId, files);
    return Results.Ok(state);
}).DisableAntiforgery();

api.MapPost("/generate", async (
    ClaimsPrincipal user,
    PixlForgeStore store,
    IHttpClientFactory httpClientFactory,
    GenerateRequest request) =>
{
    var userKey = UserKey(user);
    var state = await store.SetActiveProject(userKey, request.ProjectId);
    var settings = await store.UpdateSettings(userKey, request.Settings);
    var project = state.Projects.FirstOrDefault(project => project.Id == request.ProjectId)
        ?? throw new InvalidOperationException("Project not found.");
    var apiKey = await store.GetOpenAiApiKey(userKey);
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        throw new InvalidOperationException("Add an OpenAI API key before generating images.");
    }

    var prompt = await store.BuildPromptWithReferences(project, request.Prompt);
    var count = Math.Clamp(settings.Count, 1, 10);
    var model = string.IsNullOrWhiteSpace(settings.OpenAiModel) ? "gpt-image-1" : settings.OpenAiModel.Trim();
    var body = new Dictionary<string, object?>
    {
        ["model"] = model,
        ["prompt"] = prompt,
        ["n"] = count,
        ["size"] = NormalizeOpenAiSize(settings.OpenAiSize, settings.AspectRatio)
    };
    if (!string.IsNullOrWhiteSpace(settings.OpenAiQuality) && settings.OpenAiQuality != "auto") body["quality"] = settings.OpenAiQuality;
    if (!string.IsNullOrWhiteSpace(settings.OpenAiFormat) && settings.OpenAiFormat != "png") body["output_format"] = settings.OpenAiFormat;
    if (!string.IsNullOrWhiteSpace(settings.OpenAiModeration) && settings.OpenAiModeration != "auto") body["moderation"] = settings.OpenAiModeration;

    var client = httpClientFactory.CreateClient();
    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations");
    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    httpRequest.Content = new StringContent(JsonSerializer.Serialize(body, JsonDefaults.Options), Encoding.UTF8, "application/json");

    using var response = await client.SendAsync(httpRequest);
    var responseJson = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException(ReadOpenAiError(responseJson, response.StatusCode));
    }

    var openAiResponse = JsonSerializer.Deserialize<OpenAiImageResponse>(responseJson, JsonDefaults.Options)
        ?? new OpenAiImageResponse([]);
    var generations = await store.SaveOpenAiGenerations(userKey, project.Id, request.Prompt, settings, openAiResponse.Data);
    return Results.Ok(new GenerateResponse(generations));
});

api.MapPost("/upscale", async (ClaimsPrincipal user, PixlForgeStore store, UpscaleRequest request) =>
{
    var generations = await store.Upscale(UserKey(user), request.ProjectId, request.GenerationIds);
    return Results.Ok(new GenerateResponse(generations));
});

api.MapDelete("/generations/{generationId}", async (ClaimsPrincipal user, PixlForgeStore store, string generationId) =>
    Results.Ok(await store.DeleteGeneration(UserKey(user), generationId)));

app.MapGet("/api/assets/{assetId}", async (PixlForgeStore store, string assetId) =>
{
    var asset = await store.ResolveAsset(assetId);
    if (asset is null || !File.Exists(asset.Path)) return Results.NotFound();
    return Results.File(asset.Path, asset.ContentType);
});

app.MapFallbackToFile("index.html");
app.Run();

static string UserKey(ClaimsPrincipal user)
{
    var id = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? "anonymous";
    return Convert.ToHexString(Encoding.UTF8.GetBytes(id)).ToLowerInvariant();
}

static string NormalizeOpenAiSize(string size, string aspectRatio)
{
    if (new[] { "1024x1024", "1024x1536", "1536x1024", "auto" }.Contains(size)) return size;
    return aspectRatio switch
    {
        "16:9" or "4:3" => "1536x1024",
        "9:16" or "3:4" => "1024x1536",
        _ => "1024x1024"
    };
}

static string ReadOpenAiError(string json, System.Net.HttpStatusCode statusCode)
{
    try
    {
        var parsed = JsonSerializer.Deserialize<OpenAiErrorResponse>(json, JsonDefaults.Options);
        return parsed?.Error?.Message ?? $"OpenAI request failed with HTTP {(int)statusCode}.";
    }
    catch
    {
        return $"OpenAI request failed with HTTP {(int)statusCode}.";
    }
}

sealed class PixlForgeStore(string dataRoot)
{
    private readonly string dataRoot = dataRoot;

    public async Task<PixlForgeState> Load(string userKey)
    {
        var root = UserRoot(userKey);
        Directory.CreateDirectory(root);
        var statePath = StatePath(userKey);
        if (!File.Exists(statePath))
        {
            var state = DefaultState(userKey);
            await Save(userKey, state);
            return state;
        }

        await using var stream = File.OpenRead(statePath);
        var loaded = await JsonSerializer.DeserializeAsync<PixlForgeState>(stream, JsonDefaults.Options) ?? DefaultState(userKey);
        loaded.Settings = NormalizeSettings(loaded.Settings);
        if (loaded.Projects.Count == 0) loaded.Projects.Add(DefaultProject(userKey, loaded.Settings));
        if (!loaded.Projects.Any(project => project.Id == loaded.ActiveProjectId)) loaded.ActiveProjectId = loaded.Projects[0].Id;
        return loaded;
    }

    public async Task Save(string userKey, PixlForgeState state)
    {
        Directory.CreateDirectory(UserRoot(userKey));
        await using var stream = File.Create(StatePath(userKey));
        await JsonSerializer.SerializeAsync(stream, state, JsonDefaults.Options);
    }

    public async Task<PixlForgeSettings> UpdateSettings(string userKey, PixlForgeSettings settings)
    {
        var state = await Load(userKey);
        state.Settings = NormalizeSettings(settings);
        await Save(userKey, state);
        return state.Settings;
    }

    public async Task<PixlForgeState> CreateProject(string userKey, string name)
    {
        var state = await Load(userKey);
        var projectName = string.IsNullOrWhiteSpace(name) ? "Untitled Project" : name.Trim();
        var project = new PixlForgeProject(
            Guid.NewGuid().ToString("n"),
            projectName,
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        state.Projects.Insert(0, project);
        state.ActiveProjectId = project.Id;
        Directory.CreateDirectory(ProjectRoot(userKey, project.Id));
        await Save(userKey, state);
        return state;
    }

    public async Task<PixlForgeState> SetActiveProject(string userKey, string projectId)
    {
        var state = await Load(userKey);
        if (!state.Projects.Any(project => project.Id == projectId)) throw new InvalidOperationException("Project not found.");
        state.ActiveProjectId = projectId;
        await Save(userKey, state);
        return state;
    }

    public async Task<PixlForgeState> DeleteProject(string userKey, string projectId)
    {
        var state = await Load(userKey);
        if (state.Projects.Count <= 1) throw new InvalidOperationException("At least one project is required.");
        state.Projects = state.Projects.Where(project => project.Id != projectId).ToList();
        state.Generations = state.Generations.Where(generation => generation.ProjectId != projectId).ToList();
        state.ActiveProjectId = state.Projects[0].Id;
        var root = ProjectRoot(userKey, projectId);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        await Save(userKey, state);
        return state;
    }

    public async Task<PixlForgeState> AddReferenceFiles(string userKey, string projectId, IFormFileCollection files)
    {
        var state = await Load(userKey);
        var project = state.Projects.FirstOrDefault(project => project.Id == projectId)
            ?? throw new InvalidOperationException("Project not found.");
        var referenceRoot = Path.Combine(ProjectRoot(userKey, projectId), "references");
        Directory.CreateDirectory(referenceRoot);

        foreach (var file in files)
        {
            if (file.Length <= 0) continue;
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var id = Guid.NewGuid().ToString("n");
            var fileName = $"{PixlForgeHelpers.Slug(Path.GetFileNameWithoutExtension(file.FileName))}-{id[..8]}{extension}";
            var target = Path.Combine(referenceRoot, fileName);
            await using (var stream = File.Create(target))
            {
                await file.CopyToAsync(stream);
            }

            project.References.Insert(0, new ReferenceFile(
                id,
                Path.GetFileName(file.FileName),
                AssetId(target),
                file.ContentType,
                file.Length,
                DateTimeOffset.UtcNow));
        }

        project.UpdatedAt = DateTimeOffset.UtcNow;
        await Save(userKey, state);
        return state;
    }

    public async Task<PixlForgeState> RemoveReference(string userKey, string projectId, string referenceId)
    {
        var state = await Load(userKey);
        var project = state.Projects.FirstOrDefault(project => project.Id == projectId)
            ?? throw new InvalidOperationException("Project not found.");
        var reference = project.References.FirstOrDefault(candidate => candidate.Id == referenceId);
        if (reference is not null)
        {
            var path = AssetPath(reference.AssetId);
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        project.References = project.References.Where(candidate => candidate.Id != referenceId).ToList();
        await Save(userKey, state);
        return state;
    }

    public async Task<string> BuildPromptWithReferences(PixlForgeProject project, string prompt)
    {
        var snippets = new List<string>();
        foreach (var reference in project.References.Take(12))
        {
            var path = AssetPath(reference.AssetId);
            if (path is null || !File.Exists(path)) continue;
            if (!reference.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
                !new[] { ".txt", ".md", ".json", ".csv", ".svg", ".html", ".xml", ".yaml", ".yml" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                continue;
            }

            var text = await File.ReadAllTextAsync(path);
            if (text.Length > 2500) text = text[..2500] + "\n[truncated]";
            snippets.Add($"{reference.Name}:\n{text}");
        }

        if (snippets.Count == 0) return prompt.Trim();
        return $"{prompt.Trim()}\n\nReference document excerpts:\n{string.Join("\n\n", snippets)}";
    }

    public async Task<List<ImageGeneration>> SaveOpenAiGenerations(string userKey, string projectId, string prompt, PixlForgeSettings settings, List<OpenAiImageData> images)
    {
        var state = await Load(userKey);
        var outputRoot = Path.Combine(ProjectRoot(userKey, projectId), "generations", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outputRoot);
        var batchId = Guid.NewGuid().ToString("n");
        var created = new List<ImageGeneration>();
        for (var index = 0; index < images.Count; index++)
        {
            var item = images[index];
            var bytes = !string.IsNullOrWhiteSpace(item.B64Json)
                ? Convert.FromBase64String(item.B64Json)
                : await DownloadImage(item.Url);
            var extension = settings.OpenAiFormat == "jpeg" ? ".jpg" : settings.OpenAiFormat == "webp" ? ".webp" : ".png";
            var path = Path.Combine(outputRoot, $"{PixlForgeHelpers.Slug(prompt)}-{index + 1}{extension}");
            await File.WriteAllBytesAsync(path, bytes);
            created.Add(new ImageGeneration(
                Guid.NewGuid().ToString("n"),
                projectId,
                prompt,
                "openai",
                "draft",
                "",
                AssetId(path),
                "completed",
                "",
                DateTimeOffset.UtcNow,
                settings.OpenAiModel,
                settings.OpenAiSize,
                batchId,
                index + 1));
        }

        state.Generations = created.Concat(state.Generations).Take(400).ToList();
        await Save(userKey, state);
        return created;
    }

    public async Task<List<ImageGeneration>> Upscale(string userKey, string projectId, List<string> generationIds)
    {
        var state = await Load(userKey);
        var selected = state.Generations
            .Where(generation => generation.ProjectId == projectId && generation.Kind != "final" && generationIds.Contains(generation.Id))
            .ToList();
        if (selected.Count == 0) throw new InvalidOperationException("Select at least one draft first.");
        var outputRoot = Path.Combine(ProjectRoot(userKey, projectId), "upscaled", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outputRoot);
        var batchId = Guid.NewGuid().ToString("n");
        var results = new List<ImageGeneration>();
        for (var index = 0; index < selected.Count; index++)
        {
            var source = selected[index];
            var sourcePath = AssetPath(source.AssetId) ?? "";
            if (!File.Exists(sourcePath)) continue;
            using var image = SKBitmap.Decode(sourcePath);
            if (image is null) continue;
            var target = Get4kTarget(image.Width, image.Height);
            var outputPath = Path.Combine(outputRoot, $"{Path.GetFileNameWithoutExtension(sourcePath)}-4k-{index + 1}.png");
            using var scaled = image.Resize(new SKImageInfo(target.Width, target.Height), SKSamplingOptions.Default);
            if (scaled is null) continue;
            using var skImage = SKImage.FromBitmap(scaled);
            await using var output = File.Create(outputPath);
            skImage.Encode(SKEncodedImageFormat.Png, 95).SaveTo(output);
            results.Add(new ImageGeneration(
                Guid.NewGuid().ToString("n"),
                projectId,
                source.Prompt,
                "local",
                "final",
                source.Id,
                AssetId(outputPath),
                "completed",
                "",
                DateTimeOffset.UtcNow,
                "PixlForge local upscaler",
                $"{target.Width}x{target.Height}",
                batchId,
                index + 1));
        }

        state.Generations = results.Concat(state.Generations).Take(400).ToList();
        await Save(userKey, state);
        return results;
    }

    public async Task<PixlForgeState> DeleteGeneration(string userKey, string generationId)
    {
        var state = await Load(userKey);
        var generation = state.Generations.FirstOrDefault(candidate => candidate.Id == generationId);
        if (generation is not null)
        {
            var path = AssetPath(generation.AssetId);
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        state.Generations = state.Generations.Where(candidate => candidate.Id != generationId).ToList();
        await Save(userKey, state);
        return state;
    }

    public async Task<SecretStatus> GetSecretStatus(string userKey)
    {
        var envKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        return new SecretStatus(File.Exists(OpenAiKeyPath(userKey)), envKey);
    }

    public async Task<string> GetOpenAiApiKey(string userKey)
    {
        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey)) return envKey.Trim();
        var path = OpenAiKeyPath(userKey);
        return File.Exists(path) ? (await File.ReadAllTextAsync(path)).Trim() : "";
    }

    public async Task<SecretStatus> SaveOpenAiApiKey(string userKey, string apiKey)
    {
        Directory.CreateDirectory(UserRoot(userKey));
        if (string.IsNullOrWhiteSpace(apiKey)) return await ClearOpenAiApiKey(userKey);
        await File.WriteAllTextAsync(OpenAiKeyPath(userKey), apiKey.Trim());
        return await GetSecretStatus(userKey);
    }

    public async Task<SecretStatus> ClearOpenAiApiKey(string userKey)
    {
        var path = OpenAiKeyPath(userKey);
        if (File.Exists(path)) File.Delete(path);
        return await GetSecretStatus(userKey);
    }

    public Task<ResolvedAsset?> ResolveAsset(string assetId)
    {
        var path = AssetPath(assetId);
        if (path is null || !Path.GetFullPath(path).StartsWith(Path.GetFullPath(dataRoot))) return Task.FromResult<ResolvedAsset?>(null);
        return Task.FromResult<ResolvedAsset?>(new ResolvedAsset(path, PixlForgeHelpers.ContentType(path)));
    }

    private PixlForgeState DefaultState(string userKey)
    {
        var settings = NormalizeSettings(null);
        return new PixlForgeState(settings, [DefaultProject(userKey, settings)], "default-project", []);
    }

    private PixlForgeProject DefaultProject(string userKey, PixlForgeSettings settings)
    {
        Directory.CreateDirectory(ProjectRoot(userKey, "default-project"));
        return new PixlForgeProject("default-project", "Default Project", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private static PixlForgeSettings NormalizeSettings(PixlForgeSettings? settings)
    {
        var merged = settings ?? new PixlForgeSettings();
        merged.OpenAiModel = string.IsNullOrWhiteSpace(merged.OpenAiModel) ? "gpt-image-1" : merged.OpenAiModel;
        merged.OpenAiSize = string.IsNullOrWhiteSpace(merged.OpenAiSize) ? "1024x1024" : merged.OpenAiSize;
        merged.OpenAiQuality = string.IsNullOrWhiteSpace(merged.OpenAiQuality) ? "auto" : merged.OpenAiQuality;
        merged.OpenAiFormat = string.IsNullOrWhiteSpace(merged.OpenAiFormat) ? "png" : merged.OpenAiFormat;
        merged.OpenAiModeration = string.IsNullOrWhiteSpace(merged.OpenAiModeration) ? "auto" : merged.OpenAiModeration;
        merged.AspectRatio = new[] { "1:1", "16:9", "9:16", "4:3", "3:4" }.Contains(merged.AspectRatio) ? merged.AspectRatio : "1:1";
        merged.Count = Math.Clamp(merged.Count <= 0 ? 3 : merged.Count, 1, 10);
        return merged;
    }

    private string UserRoot(string userKey) => Path.Combine(dataRoot, "users", userKey);
    private string StatePath(string userKey) => Path.Combine(UserRoot(userKey), "state.json");
    private string OpenAiKeyPath(string userKey) => Path.Combine(UserRoot(userKey), "openai.key");
    private string ProjectRoot(string userKey, string projectId) => Path.Combine(UserRoot(userKey), "projects", projectId);

    private string AssetId(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fullPath))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(dataRoot, $"{id}.asset"), fullPath);
        return id;
    }

    private string? AssetPath(string assetId)
    {
        var map = Path.Combine(dataRoot, $"{assetId}.asset");
        return File.Exists(map) ? File.ReadAllText(map) : null;
    }

    private static (int Width, int Height) Get4kTarget(int width, int height)
    {
        var longEdge = width == height ? 4096d : 3840d;
        var scale = Math.Max(1, longEdge / Math.Max(width, height));
        return (MakeEven((int)Math.Round(width * scale)), MakeEven((int)Math.Round(height * scale)));
    }

    private static int MakeEven(int value) => Math.Max(2, value % 2 == 0 ? value : value + 1);

    private static async Task<byte[]> DownloadImage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("OpenAI did not return image data.");
        using var client = new HttpClient();
        return await client.GetByteArrayAsync(url);
    }
}

static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

static class PixlForgeHelpers
{
    public static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var slug = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "pixlforge" : slug[..Math.Min(slug.Length, 80)];
    }

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".txt" or ".md" => "text/plain",
        ".json" => "application/json",
        ".pdf" => "application/pdf",
        _ => "image/png"
    };
}

sealed record UserProfile(string? Subject, string? Email, string? OrganizationId, string? Role, string[] Permissions)
{
    public static UserProfile From(ClaimsPrincipal user) => new(
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"),
        user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email"),
        user.FindFirstValue("org_id"),
        user.FindFirstValue(ClaimTypes.Role) ?? user.FindFirstValue("role"),
        user.FindAll("permissions").Select(claim => claim.Value).ToArray());
}

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
                if (DateTimeOffset.UtcNow < expiresAt && keys.Count > 0) return keys;
                var jwks = HttpClient.GetStringAsync(jwksUri).GetAwaiter().GetResult();
                keys = new JsonWebKeySet(jwks).Keys.Cast<SecurityKey>().ToArray();
                expiresAt = DateTimeOffset.UtcNow.AddHours(6);
                return keys;
            }
        };
}

sealed class PixlForgeState(PixlForgeSettings settings, List<PixlForgeProject> projects, string activeProjectId, List<ImageGeneration> generations)
{
    public PixlForgeSettings Settings { get; set; } = settings;
    public List<PixlForgeProject> Projects { get; set; } = projects;
    public string ActiveProjectId { get; set; } = activeProjectId;
    public List<ImageGeneration> Generations { get; set; } = generations;
}
sealed class PixlForgeProject(string id, string name, List<ReferenceFile> references, DateTimeOffset createdAt, DateTimeOffset updatedAt)
{
    public string Id { get; set; } = id;
    public string Name { get; set; } = name;
    public List<ReferenceFile> References { get; set; } = references;
    public DateTimeOffset CreatedAt { get; set; } = createdAt;
    public DateTimeOffset UpdatedAt { get; set; } = updatedAt;
}
sealed record ReferenceFile(string Id, string Name, string AssetId, string ContentType, long Size, DateTimeOffset AddedAt);
sealed record ImageGeneration(string Id, string ProjectId, string Prompt, string Provider, string Kind, string ParentGenerationId, string AssetId, string Status, string Error, DateTimeOffset CreatedAt, string Model, string Size, string BatchId, int Index);
sealed class PixlForgeSettings
{
    public int Count { get; set; } = 3;
    public string AspectRatio { get; set; } = "1:1";
    public string OpenAiModel { get; set; } = "gpt-image-1";
    public string OpenAiSize { get; set; } = "1024x1024";
    public string OpenAiQuality { get; set; } = "auto";
    public string OpenAiFormat { get; set; } = "png";
    public string OpenAiModeration { get; set; } = "auto";
}
sealed record SecretStatus(bool OpenAiApiKeySaved, bool OpenAiApiKeyFromEnv);
sealed record CreateProjectRequest(string Name);
sealed record SaveOpenAiKeyRequest(string ApiKey);
sealed record GenerateRequest(string ProjectId, string Prompt, PixlForgeSettings Settings);
sealed record UpscaleRequest(string ProjectId, List<string> GenerationIds);
sealed record GenerateResponse(List<ImageGeneration> Generations);
sealed record ResolvedAsset(string Path, string ContentType);
sealed record OpenAiImageResponse(List<OpenAiImageData> Data);
sealed record OpenAiImageData([property: JsonPropertyName("b64_json")] string? B64Json, string? Url);
sealed record OpenAiErrorResponse(OpenAiError? Error);
sealed record OpenAiError(string? Message);
