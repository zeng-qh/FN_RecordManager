using Microsoft.AspNetCore.Http.Features;
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

// 网页端设置持久化：settings.json 存在时覆盖 ASR 配置（网页端保存 > 部署默认值）
var settingsStore = new SettingsStore(cfg.DataDir);
if (settingsStore.Load() is { } savedAsr)
{
    cfg.Asr = savedAsr;
}

builder.WebHost.UseUrls($"http://{cfg.Bind}:{cfg.Port}");

// 依赖注入
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(settingsStore);
builder.Services.AddSingleton(new DbService(cfg.DataDir));
builder.Services.AddSingleton(new StorageService(cfg.DataDir));
builder.Services.AddSingleton(new AuthService(cfg.Token));
builder.Services.AddHttpClient<AsrClient>();
builder.Services.AddSingleton<TranscriptionQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TranscriptionQueue>());
builder.Services.AddSingleton<IdleScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IdleScheduler>());
builder.Services.AddSingleton(sp =>
    new ChunkedUploadStore(cfg.DataDir, sp.GetRequiredService<ILogger<ChunkedUploadStore>>()));
builder.Services.AddLogging();

// 优雅关闭：给进行中的转写足够时间收尾（默认仅 5s 过短，会被强杀）
builder.Host.ConfigureHostOptions(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

// 大文件上传（长录音）限制：默认 134MB 太小；调到 2GB 并保证流式落盘不占内存
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024;
    o.MemoryBufferThreshold = 256 * 1024; // 超过 256KB 即缓冲到磁盘，避免整段录音进内存
});

var app = builder.Build();

var db = app.Services.GetRequiredService<DbService>();
var storage = app.Services.GetRequiredService<StorageService>();
var auth = app.Services.GetRequiredService<AuthService>();
var asr = app.Services.GetRequiredService<AsrClient>();
var queue = app.Services.GetRequiredService<TranscriptionQueue>();
var idle = app.Services.GetRequiredService<IdleScheduler>();
var chunks = app.Services.GetRequiredService<ChunkedUploadStore>();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("VoiceVault");

// 优雅关闭日志：便于确认容器 stop 时资源已正确释放
var lifetime = app.Lifetime;
lifetime.ApplicationStopping.Register(() => logger.LogInformation("Application stopping…"));
lifetime.ApplicationStopped.Register(() => logger.LogInformation("Application stopped; SQLite connections released."));

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
        var (id, sha) = await CompleteRecordingAsync(
            file.OpenReadStream(), relPath, device, expectedSha, mtime);
        return Results.Ok(new { id, status = "uploaded", sha });
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

// ---- 分片上传（网页端断点续传；弱网/大文件中断后仅补传缺失分片）----
api.MapPost("/upload/init", (UploadInitRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.RelPath) || string.IsNullOrWhiteSpace(req.Device)
        || req.Size <= 0 || req.ChunkSize <= 0 || req.TotalChunks <= 0)
        return Results.BadRequest(new { error = "relPath, device, size, chunkSize, totalChunks are required" });
    if (req.TotalChunks > 100_000)
        return Results.BadRequest(new { error = "too many chunks" });

    var (uploadId, received) = chunks.Init(
        req.Device, req.RelPath, req.Mtime, req.Size, req.ChunkSize, req.TotalChunks);
    logger.LogInformation("Chunk init {RelPath}: session={Id}, resume {N}/{T}",
        req.RelPath, uploadId, received.Count, req.TotalChunks);
    return Results.Ok(new { uploadId, received, totalChunks = req.TotalChunks });
});

api.MapPut("/upload/chunk/{uploadId}/{index:long}", async (HttpContext ctx, string uploadId, long index) =>
{
    var err = await chunks.WriteChunkAsync(uploadId, index, ctx.Request.Body, ctx.RequestAborted);
    if (err is not null) return Results.BadRequest(new { error = err });
    return Results.Ok(new { index, done = true });
});

api.MapPost("/upload/complete/{uploadId}", async (string uploadId) =>
{
    var (session, missing) = chunks.GetComplete(uploadId);
    if (session is null)
        return Results.NotFound(new { error = "session not found" });
    if (missing.Count > 0)
        return Results.BadRequest(new { error = "missing chunks", missing });

    try
    {
        // 零拷贝拼接流直接交给落盘/去重/入库公共逻辑
        await using var concat = new ConcatenatedStream(session.PartPaths);
        var (id, sha) = await CompleteRecordingAsync(
            concat, session.RelPath, session.Device, "", session.Mtime);
        chunks.Remove(uploadId); // 成功后清理会话；失败保留分片可重试 complete
        return Results.Ok(new { id, status = "uploaded", sha });
    }
    catch (InvalidDataException ex)
    {
        logger.LogWarning(ex, "Chunked upload rejected: {RelPath}", session.RelPath);
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Chunked upload complete failed: {RelPath}", session.RelPath);
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

    // 手动触发总是立即执行（不受闲时窗口限制），并即时反馈状态。
    db.UpdateTranscript(id, "", "pending", null);
    queue.Enqueue(id);
    return Results.Accepted(null, new { status = "pending" });
});

// 批量转写：把所有未完成（未转写/待闲时/失败）的录音一次性入队，串行消化
api.MapPost("/transcribe/pending", () =>
{
    if (!cfg.Asr.Enabled)
        return Results.BadRequest(new { error = "asr disabled", asr = false });

    var ids = db.GetTranscribableIds();
    foreach (var id in ids)
    {
        db.UpdateTranscript(id, "", "pending", null);
        queue.Enqueue(id);
    }
    logger.LogInformation("Bulk transcribe queued: {Count} recordings", ids.Count);
    return Results.Accepted(null, new { queued = ids.Count });
});

// 删除录音：移除元数据行，并清理落盘文件与文字稿 JSON
api.MapDelete("/recordings/{id:long}", (long id) =>
{
    var rec = db.DeleteById(id);
    if (rec is null) return Results.NotFound(new { error = "not found" });

    foreach (var p in new[] { rec.StoredPath, rec.TranscriptPath })
    {
        if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
        try { File.Delete(p); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to delete file {Path}", p); }
    }
    logger.LogInformation("Deleted recording id={Id} relPath={RelPath}", id, rec.RelPath);
    return Results.Ok(new { deleted = id });
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
    cfg.Asr.Mode = incoming.Mode;
    cfg.Asr.IdleStartHour = ClampHour(incoming.IdleStartHour);
    cfg.Asr.IdleEndHour = ClampHour(incoming.IdleEndHour);
    cfg.Asr.Endpoint = incoming.Endpoint;
    cfg.Asr.Model = incoming.Model;
    cfg.Asr.SpeakerDiarization = incoming.SpeakerDiarization;

    // 持久化到 settings.json：重启容器配置不丢
    try { settingsStore.Save(cfg.Asr); }
    catch (Exception ex) { logger.LogWarning(ex, "Failed to persist settings"); }

    logger.LogInformation(
        "Settings updated: asr={Enabled}, mode={Mode}, idle={S}-{E}",
        cfg.Asr.Enabled, cfg.Asr.Mode, cfg.Asr.IdleStartHour, cfg.Asr.IdleEndHour);

    // 保存后立即巡检一次：若当前正处闲时窗口，积压无需等满 1 分钟
    idle.TriggerNow();

    return Results.Ok(new { asr = cfg.Asr });
});

/// <summary>将小时裁剪到 [0,23] 合法范围。</summary>
static int ClampHour(int h) => Math.Clamp(h, 0, 23);

/// <summary>
/// 上传收尾公共逻辑（单请求 multipart 与分片拼接共用）：
/// 流式落盘算 SHA → 入库去重 → 清理去重孤儿副本 → 按转写时机分流。
/// 成功返回 (id, sha)；校验失败抛 InvalidDataException，其余异常向上传播。
/// </summary>
async Task<(long Id, string Sha)> CompleteRecordingAsync(
    Stream content, string relPath, string device, string expectedSha, long mtime)
{
    var (storedPath, recordedAt, actualSha, size) =
        await storage.SaveAsync(content, relPath, expectedSha, mtime);

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

    // 去重命中（device+sha 已存在）时，ON CONFLICT 保留原落盘路径，
    // 本次刚写入的副本成为孤儿文件，这里清理掉
    var current = db.GetById(id);
    if (current is not null && !string.Equals(current.StoredPath, storedPath, StringComparison.Ordinal))
    {
        try
        {
            File.Delete(storedPath);
            logger.LogInformation("Dedup hit: removed duplicate copy {Path}", storedPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove duplicate copy {Path}", storedPath);
        }
    }

    // 转写触发：按用户设定的模式处理
    //  - Realtime：上传后立即入队
    //  - Idle：空闲时段内立即入队，否则标记为 idle 等待调度器在闲时统一处理
    if (cfg.Asr.Enabled && cfg.Asr.Mode == TranscribeMode.Realtime)
    {
        queue.Enqueue(id);
        logger.LogInformation("Realtime transcribe queued id={Id}", id);
    }
    else if (cfg.Asr.Enabled && cfg.Asr.Mode == TranscribeMode.Idle)
    {
        if (cfg.Asr.InIdleWindowNow())
        {
            queue.Enqueue(id);
            db.UpdateTranscript(id, "", "pending", null);
            logger.LogInformation("Idle window active, transcribe queued id={Id}", id);
        }
        else
        {
            db.UpdateTranscript(id, "", "idle", null);
            logger.LogInformation("Idle-pending id={Id} (outside idle window)", id);
        }
    }

    return (id, actualSha);
}

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
