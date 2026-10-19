using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoiceVault.Server.Models;
using VoiceVault.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// 配置来源：appsettings.json + 环境变量（VAULT_ 前缀，嵌套用 __，如 VAULT_ASR__ENABLED）
builder.Configuration.AddEnvironmentVariables(prefix: "VAULT_");

var cfg = new VaultConfig();
builder.Configuration.Bind(cfg);
cfg.DataDir = Path.GetFullPath(cfg.DataDir);

builder.WebHost.UseUrls($"http://{cfg.Bind}:{cfg.Port}");

// 依赖注入
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(new DbService(cfg.DataDir));
builder.Services.AddSingleton(new StorageService(cfg.DataDir));
builder.Services.AddSingleton(new AuthService(cfg.Token));
builder.Services.AddHttpClient<AsrClient>();
builder.Services.AddSingleton<TranscriptionQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TranscriptionQueue>());
builder.Services.AddLogging();

var app = builder.Build();

var db = app.Services.GetRequiredService<DbService>();
var storage = app.Services.GetRequiredService<StorageService>();
var auth = app.Services.GetRequiredService<AuthService>();
var asr = app.Services.GetRequiredService<AsrClient>();
var queue = app.Services.GetRequiredService<TranscriptionQueue>();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("VoiceVault");

// 鉴权中间件：除 /api/health 外的所有 /api/* 均需 Bearer Token
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Path != "/api/health"
        && !ctx.Request.Path.Value!.EndsWith("/file"))
    {
        if (!auth.Validate(ctx.Request.Headers.Authorization.ToString()))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            return;
        }
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    asr = cfg.Asr.Enabled,
    version = "1.0.0"
}));

// 增量同步：客户端上报清单，服务端返回待上传项
api.MapPost("/sync/diff", (SyncDiffRequest req) =>
{
    var toUpload = new List<string>();
    var serverHas = new List<string>();

    if (string.IsNullOrWhiteSpace(req.Device))
        return Results.BadRequest(new { error = "device is required" });

    foreach (var f in req.Files)
    {
        if (string.IsNullOrWhiteSpace(f.Sha256) || string.IsNullOrWhiteSpace(f.RelPath))
            continue;
        if (db.ExistsBySha(req.Device, f.Sha256))
            serverHas.Add(f.RelPath);
        else
            toUpload.Add(f.RelPath);
    }

    return Results.Ok(new SyncDiffResponse { ToUpload = toUpload, ServerHas = serverHas });
});

// 上传单个录音（multipart）
api.MapPost("/upload", async (HttpContext ctx) =>
{
    IFormCollection? form;
    try { form = await ctx.Request.ReadFormAsync(); }
    catch (Exception) { return Results.BadRequest(new { error = "invalid multipart form" }); }

    var relPath = form["relPath"].ToString();
    var device = form["device"].ToString();
    var expectedSha = form["sha256"].ToString();
    var mtime = long.TryParse(form["mtime"], out var m) ? m : 0;
    var file = form.Files["file"];

    if (file is null || string.IsNullOrWhiteSpace(relPath) || string.IsNullOrWhiteSpace(device))
        return Results.BadRequest(new { error = "relPath, device and file are required" });

    try
    {
        var (storedPath, recordedAt, actualSha, size) =
            await storage.SaveAsync(file.OpenReadStream(), relPath, expectedSha, mtime);

        var rec = new Recording
        {
            Device = device,
            RelPath = relPath,
            FileName = Path.GetFileName(relPath),
            StoredPath = storedPath,
            Size = size,
            Mtime = mtime,
            Sha256 = actualSha,
            Title = Path.GetFileName(relPath),
            RecordedAt = recordedAt,
            TranscriptStatus = "none",
            CreatedAt = DateTime.UtcNow
        };
        var id = db.Upsert(rec);
        logger.LogInformation("Uploaded {RelPath} -> id={Id} size={Size}", relPath, id, size);

        // 自动转写：ASR 已启用且允许自动转写时，入后台队列
        if (cfg.Asr.Enabled && cfg.Asr.AutoTranscribe)
        {
            queue.Enqueue(id);
            logger.LogInformation("Auto-transcribe queued id={Id}", id);
        }

        return Results.Ok(new { id, status = "uploaded", sha = actualSha });
    }
    catch (InvalidDataException ex)
    {
        logger.LogWarning(ex, "Upload rejected (checksum mismatch)");
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Upload failed");
        return Results.Problem(ex.Message);
    }
});

// 录音列表（分页 + 时间范围 + 关键词）
api.MapGet("/recordings", (int page = 1, int pageSize = 50, long from = 0, long to = 0, string? q = null) =>
{
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 200);
    var items = db.List(page, pageSize, from, to, q);
    var total = db.Count(from, to, q);
    return Results.Ok(new
    {
        page,
        pageSize,
        total,
        items
    });
});

// 单条录音详情
api.MapGet("/recordings/{id:long}", (long id) =>
{
    var rec = db.GetById(id);
    return rec is null ? Results.NotFound(new { error = "not found" }) : Results.Ok(rec);
});

// 音频流（支持 Range，浏览器可拖拽进度）
api.MapGet("/recordings/{id:long}/file", (HttpContext ctx, long id) =>
{
    // 浏览器 <audio> 无法携带 Authorization 头，允许 ?token= 作为等价凭证
    var headerTok = ctx.Request.Headers.Authorization.ToString();
    var queryTok = ctx.Request.Query["token"].ToString();
    if (!auth.Validate(headerTok) && !auth.Validate(queryTok))
        return Results.Unauthorized();

    var rec = db.GetById(id);
    if (rec is null || !File.Exists(rec.StoredPath))
        return Results.NotFound(new { error = "file not found" });

    var ext = Path.GetExtension(rec.StoredPath).ToLowerInvariant();
    var contentType = ext switch
    {
        ".m4a" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".flac" => "audio/flac",
        _ => "application/octet-stream"
    };
    return Results.File(rec.StoredPath, contentType,
        fileDownloadName: rec.FileName, enableRangeProcessing: true);
});

// 转写文本（若已生成）
api.MapGet("/recordings/{id:long}/transcript", (long id) =>
{
    var rec = db.GetById(id);
    if (rec is null) return Results.NotFound(new { error = "not found" });
    if (rec.TranscriptStatus != "done" || string.IsNullOrWhiteSpace(rec.TranscriptPath)
        || !File.Exists(rec.TranscriptPath))
        return Results.NotFound(new { error = "transcript not ready", status = rec.TranscriptStatus });

    var json = File.ReadAllText(rec.TranscriptPath);
    return Results.Content(json, "application/json");
});

// 触发转写（仅当 ASR 启用；任务进后台队列串行执行）
api.MapPost("/recordings/{id:long}/transcribe", (long id) =>
{
    if (!cfg.Asr.Enabled)
        return Results.BadRequest(new { error = "asr disabled", asr = false });

    var rec = db.GetById(id);
    if (rec is null) return Results.NotFound(new { error = "not found" });

    queue.Enqueue(id);
    return Results.Accepted(null, new { status = "pending" });
});

// 设置（ASR 开关等，内存级；可加持久化）
api.MapGet("/settings", () => Results.Ok(new
{
    tokenHint = cfg.Token.Length > 4 ? cfg.Token[..4] + "****" : "****",
    asr = cfg.Asr
}));

api.MapPut("/settings", (AsrConfig incoming) =>
{
    cfg.Asr.Enabled = incoming.Enabled;
    cfg.Asr.AutoTranscribe = incoming.AutoTranscribe;
    cfg.Asr.Endpoint = incoming.Endpoint;
    cfg.Asr.Model = incoming.Model;
    cfg.Asr.SpeakerDiarization = incoming.SpeakerDiarization;
    logger.LogInformation("Settings updated: asr={Enabled}, auto={Auto}",
        cfg.Asr.Enabled, cfg.Asr.AutoTranscribe);
    return Results.Ok(new { asr = cfg.Asr });
});

app.Run();

/// <summary>供测试/构建探测用的 SHA-256 工具（备而不用）。</summary>
internal static class ShaProbe
{
    public static string OfFile(string path)
    {
        using var alg = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(alg.ComputeHash(fs)).ToLowerInvariant();
    }
}
