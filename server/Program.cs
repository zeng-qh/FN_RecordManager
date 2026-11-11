using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoiceVault.Server.Models;
using VoiceVault.Server.Services;

// 服务端版本号（/api/health 返回，设置界面展示；发版时更新这里）
const string AppVersion = "1.2.1";

var builder = WebApplication.CreateBuilder(args);

// 配置来源：appsettings.json + 环境变量（VAULT_ 前缀，嵌套用 __，如 VAULT_ASR__ENABLED）
builder.Configuration.AddEnvironmentVariables(prefix: "VAULT_");

var cfg = new VaultConfig();
builder.Configuration.Bind(cfg);
cfg.DataDir = Path.GetFullPath(cfg.DataDir);

// 网页端设置持久化：settings.json 存在时覆盖 ASR/保留策略/分页配置（网页端保存 > 部署默认值）
var settingsStore = new SettingsStore(cfg.DataDir);
if (settingsStore.Load() is { } saved)
{
    cfg.Asr = saved.Asr;
    cfg.Retention = saved.Retention;
    if (saved.PageSize > 0) cfg.PageSize = saved.PageSize;
    if (saved.TranscribeSpeed > 0) cfg.TranscribeSpeed = saved.TranscribeSpeed; // 转写速度因子（估算耗时用）
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
builder.Services.AddSingleton<RetentionCleanup>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionCleanup>());
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

// 响应压缩（Brotli/Gzip）：列表与文字稿 JSON 体积大降（长文字稿可到数百 KB），
// 弱网/公网访问体感明显。默认 MIME 白名单只含 text/* 与 JSON 类，
// 音频（audio/*）与 ZIP（application/zip）本身已压缩，不在列表中不会被误压。
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});

var app = builder.Build();

var db = app.Services.GetRequiredService<DbService>();
var storage = app.Services.GetRequiredService<StorageService>();
var auth = app.Services.GetRequiredService<AuthService>();
var asr = app.Services.GetRequiredService<AsrClient>();
var queue = app.Services.GetRequiredService<TranscriptionQueue>();
var idle = app.Services.GetRequiredService<IdleScheduler>();
var chunks = app.Services.GetRequiredService<ChunkedUploadStore>();
var retention = app.Services.GetRequiredService<RetentionCleanup>();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("VoiceVault");

// 优雅关闭日志：便于确认容器 stop 时资源已正确释放
var lifetime = app.Lifetime;
lifetime.ApplicationStopping.Register(() => logger.LogInformation("Application stopping…"));
lifetime.ApplicationStopped.Register(() => logger.LogInformation("Application stopped; SQLite connections released."));

// 安全响应头：静态页与 API 响应统一附加（不改变功能，防御基础项）
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

// 鉴权中间件：除 /api/health 外的所有 /api/* 均需 Bearer Token。
// Authorization 头优先；部分反向代理会剥离该头（如沙箱分享链接），
// 允许 ?token= 查询参数作为等价凭证（音频 <audio> 标签同样携带 ?token= 播放）。
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Path != "/api/health")
    {
        var headerOk = auth.Validate(ctx.Request.Headers.Authorization.ToString());
        var queryOk = auth.Validate(ctx.Request.Query["token"].ToString());
        if (!headerOk && !queryOk)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            return;
        }
    }
    await next();
});

// 压缩中间件需在响应写入前注册；置于静态文件之前使其一并生效
app.UseResponseCompression();

// 静态资源协商缓存：Cache-Control: no-cache 让浏览器每次带 ETag 校验，
// 命中返回 304（省流量），服务端更新后立即可见——无需手动强刷
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers["Cache-Control"] = "no-cache"
});

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

var api = app.MapGroup("/api");

// 启动时孤儿转写清理：transcripts/{id}.json 无对应录音记录时删除。
// 历史删除失败或竞态留下的残留文件，占空间且无消费方；现删除链路已级联，此处仅兜底。
try
{
    var tDir = Path.Combine(cfg.DataDir, "transcripts");
    if (Directory.Exists(tDir))
    {
        foreach (var f in Directory.EnumerateFiles(tDir, "*.json"))
        {
            // 只处理 {id}.json 命名的本服务产物；其余文件不动
            if (!long.TryParse(Path.GetFileNameWithoutExtension(f), out var tid) || tid <= 0)
                continue;
            if (db.GetById(tid) is not null) continue;
            try
            {
                File.Delete(f);
                logger.LogInformation("Orphan transcript removed: {File}", f);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove orphan transcript {File}", f);
            }
        }
    }
}
catch (Exception ex) { logger.LogWarning(ex, "Orphan transcript cleanup failed"); }

// 启动时存量数据分类回填：contact 为空的记录按 relPath 提取（幂等，一次完成）
try
{
    var candidates = db.GetContactBackfillCandidates();
    if (candidates.Count > 0)
    {
        var filled = 0;
        foreach (var (id, rel) in candidates)
        {
            var (ct, kind) = ContactExtractor.Extract(rel);
            if (ct is not null) { db.SetContact(id, ct, kind); filled++; }
            else db.SetContact(id, null, null); // 置空串避免反复扫描（NULL 会再次成为候选）
        }
        logger.LogInformation("Contact backfill: {Filled}/{Total} recordings classified", filled, candidates.Count);
    }
}
catch (Exception ex) { logger.LogWarning(ex, "Contact backfill failed"); }

api.MapGet("/health", () =>
{
    // NAS 部署核心水位：磁盘剩余空间、录音数与总占用（供监控与容量规划参考）。
    // 复用 GetStats 单次聚合，避免 Count + TotalBytes 两次开连接。
    long diskFree = 0;
    try
    {
        var di = new DriveInfo(Path.GetPathRoot(cfg.DataDir) ?? "/");
        if (di.IsReady) diskFree = di.AvailableFreeSpace;
    }
    catch { /* 磁盘信息不可得时不阻塞健康检查 */ }

    var (count, bytes, _, _) = db.GetStats();
    return Results.Ok(new
    {
        status = "ok",
        asr = cfg.Asr.Enabled,
        version = AppVersion,
        diskFreeBytes = diskFree,
        recordings = count,
        totalBytes = bytes
    });
});

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
        var (id, sha, dedup) = await CompleteRecordingAsync(
            file.OpenReadStream(), relPath, device, expectedSha, mtime);
        return Results.Ok(new { id, status = "uploaded", sha, dedup });
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

// 秒传预校验：客户端上传前先本地算 SHA-256，命中（库内已有同哈希文件，不限设备）
// 则直接跳过上传。仅做探测不建记录——同内容文件在库中必有对应条目。
// 返回 (id, sha)：id 供秒传后对原记录做时长兜底回填。
api.MapPost("/upload/exists", (List<string> shas) =>
{
    var list = shas.Where(s => !string.IsNullOrWhiteSpace(s))
        .Select(s => s.ToLowerInvariant()).Distinct().Take(1000).ToList();
    return Results.Ok(new
    {
        existing = db.ExistingShas(list).Select(x => new { id = x.Id, sha = x.Sha.ToLowerInvariant() })
    });
});

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
    var err = await chunks.WriteChunkAsync(
        uploadId, index, ctx.Request.Body, ctx.Request.ContentLength, ctx.RequestAborted);
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

    // 分片总大小与声明大小校验：不一致说明分片损坏/重复/截断，拒绝合并入库
    long totalBytes;
    try { totalBytes = session.PartPaths.Sum(p => new FileInfo(p).Length); }
    catch (Exception) { return Results.BadRequest(new { error = "chunk files unreadable" }); }
    if (totalBytes != session.Size)
    {
        logger.LogWarning(
            "Chunk complete size mismatch {RelPath}: declared={Declared} actual={Actual}",
            session.RelPath, session.Size, totalBytes);
        return Results.BadRequest(new
        {
            error = "chunk size mismatch (corrupted upload)",
            expected = session.Size, actual = totalBytes
        });
    }

    try
    {
        // 零拷贝拼接流直接交给落盘/去重/入库公共逻辑
        await using var concat = new ConcatenatedStream(session.PartPaths);
        var (id, sha, dedup) = await CompleteRecordingAsync(
            concat, session.RelPath, session.Device, "", session.Mtime);
        chunks.Remove(uploadId); // 成功后清理会话；失败保留分片可重试 complete
        return Results.Ok(new { id, status = "uploaded", sha, dedup });
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

// 全库聚合统计（前端顶部统计条）
api.MapGet("/stats", () =>
{
    var (count, bytes, dur, transcribed) = db.GetStats();
    return Results.Ok(new
    {
        count,
        totalBytes = bytes,
        totalDurationSec = dur,
        transcribed
    });
});

// 来源分类聚合：联系人/电话号码 → 条数（前端分类下拉）
api.MapGet("/contacts", () => Results.Ok(new
{
    items = db.GetContacts()
}));

// 月份聚合（按录音时间）：2026-11 → 条数（前端按月筛选下拉）
api.MapGet("/months", () => Results.Ok(new
{
    items = db.GetMonths()
}));

// 录音列表（分页 + 时间范围 + 关键词 + 来源分类）
api.MapGet("/recordings", (int page = 1, int pageSize = 50, long from = 0, long to = 0,
    string? q = null, string? contact = null) =>
{
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 200);
    var items = db.List(page, pageSize, from, to, q, contact);
    var total = db.Count(from, to, q, contact);
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
        ".m4a" or ".m4b" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" or ".oga" or ".opus" => "audio/ogg",
        ".flac" => "audio/flac",
        ".aac" => "audio/aac",       // 微信/小米录音常见格式，octet-stream 会导致浏览器拒播
        ".webm" => "audio/webm",
        ".amr" => "audio/amr",
        ".3gp" => "audio/3gpp",
        _ => "application/octet-stream"
    };
    return Results.File(rec.StoredPath, contentType,
        fileDownloadName: rec.FileName, enableRangeProcessing: true);
});

// 转写文本（若已生成；转写进行中返回进度与已转出的部分文本，供前端流式展示）
api.MapGet("/recordings/{id:long}/transcript", (long id) =>
{
    var rec = db.GetById(id);
    if (rec is null) return Results.NotFound(new { error = "not found" });
    if (rec.TranscriptStatus != "done" || string.IsNullOrWhiteSpace(rec.TranscriptPath)
        || !File.Exists(rec.TranscriptPath))
        return Results.NotFound(new
        {
            error = "transcript not ready",
            status = rec.TranscriptStatus,
            progress = rec.TranscriptProgress,
            partial = rec.TranscriptPartial ?? "",
        });

    var json = File.ReadAllText(rec.TranscriptPath);
    // 动态注入注册名：文件里存的始终是边车原始输出（说话人N + 簇声纹），
    // 认名在响应时按当前注册库实时比对——注册/改名/删除即刻生效于全部历史记录
    var result = JsonSerializer.Deserialize<AsrResult>(json, jsonOptions);
    if (result is null) return Results.Content(json, "application/json");
    ApplySpeakerNames(result, db, cfg);
    return Results.Json(result, jsonOptions);
});

// 触发转写（仅当 ASR 启用；任务进后台队列串行执行）
api.MapPost("/recordings/{id:long}/transcribe", (long id) =>
{
    if (!cfg.Asr.Enabled)
        return Results.BadRequest(new { error = "asr disabled", asr = false });

    var rec = db.GetById(id);
    if (rec is null) return Results.NotFound(new { error = "not found" });

    // 手动触发总是立即执行（不受闲时窗口限制），并重置重试计数、即时反馈状态。
    db.UpdateTranscript(id, "", "pending", null, attemptsDelta: -1_000_000);
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
        db.UpdateTranscript(id, "", "pending", null, attemptsDelta: -1_000_000);
        queue.Enqueue(id);
    }
    logger.LogInformation("Bulk transcribe queued: {Count} recordings", ids.Count);
    return Results.Accepted(null, new { queued = ids.Count });
});

// ---- 转写管理（弹窗）----

// 转写列表：status=todo 待转写（默认）/ pending 转写中 / done 已转写 / skip 不转写 / all 全部。
// 附各状态计数、待转写总时长与速度因子（前端估算预计耗时）；now 供前端校准「已进行」计时。
api.MapGet("/transcribe/list", (string? status) =>
{
    var (todo, pending, done, error, skipped, todoDur) = db.TranscribeCounts();
    return Results.Ok(new
    {
        items = db.ListForTranscribe(status ?? "todo"),
        counts = new { todo, pending, done, error, skipped, todoDurationSec = todoDur },
        speed = Math.Round(cfg.TranscribeSpeed, 2),
        now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    });
});

// 批量入队转写：{ all: true } 一键全部未转写（自动跳过「不转写」标记），或 { ids: [...] }
api.MapPost("/transcribe/enqueue", (TranscribeEnqueueRequest req) =>
{
    if (!cfg.Asr.Enabled)
        return Results.BadRequest(new { error = "asr disabled", asr = false });

    List<long> ids;
    var skipped = 0;
    if (req.All)
    {
        ids = db.GetTranscribableIds();
    }
    else
    {
        ids = [];
        foreach (var id in (req.Ids ?? []).Distinct().Take(2000))
        {
            var rec = db.GetById(id);
            // 「不转写」标记的记录批量操作一律跳过（需先取消标记）；已转写/进行中的同样忽略
            if (rec is null || rec.NoTranscribe || rec.TranscriptStatus is "pending" or "done")
            {
                skipped++;
                continue;
            }
            ids.Add(id);
        }
    }
    foreach (var id in ids)
    {
        db.UpdateTranscript(id, "", "pending", null, attemptsDelta: -1_000_000); // 手动触发重置重试计数
        queue.Enqueue(id);
    }
    logger.LogInformation("Transcribe enqueue: queued={Queued} skipped={Skipped} all={All}",
        ids.Count, skipped, req.All);
    return Results.Accepted(null, new { queued = ids.Count, skipped });
});

// 批量设置「默认不转写」标记（value=false 取消）。标记后一键/批量转写自动跳过
api.MapPost("/transcribe/skip", (TranscribeSkipRequest req) =>
{
    var n = db.SetNoTranscribe((req.Ids ?? []).Distinct().Take(2000).ToList(), req.Value);
    logger.LogInformation("No-transcribe mark: {Count} recordings -> {Value}", n, req.Value);
    return Results.Ok(new { updated = n });
});

// 时长批量回填：前端经 <audio> 元数据读出录音时长后写回
// （未转写录音本无时长，估算预计耗时需要它；仅允许写入空缺值，不覆盖转写得到的精确时长）
api.MapPost("/recordings/durations", (List<DurationPatch> list) =>
{
    if (list.Count == 0) return Results.Ok(new { updated = 0 });
    var n = db.SetDurations(list
        .Where(d => d.Id > 0 && d.DurationSec > 0)
        .Take(500)
        .Select(d => (d.Id, Math.Clamp(d.DurationSec, 0, 86400.0 * 31)))
        .ToList());
    return Results.Ok(new { updated = n });
});

// 更新标题 / 备注 / 分类（传 null 不修改，空字符串清空；备注参与关键词搜索）
api.MapPatch("/recordings/{id:long}", (long id, UpdateMetaRequest req) =>
{
    var rec = db.GetById(id);
    if (rec is null) return Results.NotFound(new { error = "not found" });

    // 手动设置分类时推断 kind：纯数字 → 电话，否则微信联系人
    string? contact = req.Contact, kind = null;
    if (!string.IsNullOrWhiteSpace(contact))
        kind = contact.All(char.IsDigit) ? "phone" : "wechat";

    db.UpdateMeta(id, req.Title, req.Note, contact, kind);
    return Results.Ok(db.GetById(id));
});

// 导出文字稿：format=txt（带时间戳纯文本）| srt（标准字幕）
api.MapGet("/recordings/{id:long}/transcript/export", (long id, string? format) =>
{
    var rec = db.GetById(id);
    if (rec is null) return Results.NotFound(new { error = "not found" });
    if (rec.TranscriptStatus != "done" || string.IsNullOrWhiteSpace(rec.TranscriptPath)
        || !File.Exists(rec.TranscriptPath))
        return Results.BadRequest(new { error = "transcript not ready", status = rec.TranscriptStatus });

    var json = File.ReadAllText(rec.TranscriptPath);
    var result = JsonSerializer.Deserialize<AsrResult>(json, jsonOptions);
    if (result is null) return Results.Problem("transcript file corrupted");
    ApplySpeakerNames(result, db, cfg); // 导出内容同样显示注册名

    var isSrt = string.Equals(format, "srt", StringComparison.OrdinalIgnoreCase);
    var text = isSrt ? BuildSrt(result) : BuildPlainText(result);
    var ext = isSrt ? "srt" : "txt";
    var baseName = Path.GetFileNameWithoutExtension(
        string.IsNullOrWhiteSpace(rec.Title) ? rec.FileName : rec.Title);
    return Results.File(
        Encoding.UTF8.GetBytes(text), $"text/plain; charset=utf-8",
        fileDownloadName: $"{SanitizeFileName(baseName)}.{ext}");
});

// 删除录音：移除元数据行，并清理落盘文件与文字稿 JSON
api.MapDelete("/recordings/{id:long}", (long id) =>
{
    var rec = db.DeleteById(id);
    if (rec is null) return Results.NotFound(new { error = "not found" });

    // 声纹注册样本级联清理（来源引用不悬空；仅剩该来源的空档案一并移除）
    db.DeleteSpeakerSamplesByRecord(id);

    foreach (var p in new[] { rec.StoredPath, rec.TranscriptPath })
    {
        if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
        try { File.Delete(p); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to delete file {Path}", p); }
    }
    logger.LogInformation("Deleted recording id={Id} relPath={RelPath}", id, rec.RelPath);
    return Results.Ok(new { deleted = id });
});

// 批量删除：网页端多选模式一次删除多条（逐条走同一清理逻辑，幂等）
api.MapPost("/recordings/batch-delete", (List<long> ids) =>
{
    var deleted = 0;
    var notFound = new List<long>();
    foreach (var id in ids.Distinct().Take(1000))
    {
        var rec = db.DeleteById(id);
        if (rec is null) { notFound.Add(id); continue; }
        db.DeleteSpeakerSamplesByRecord(id); // 声纹注册样本级联清理
        foreach (var p in new[] { rec.StoredPath, rec.TranscriptPath })
        {
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
            try { File.Delete(p); }
            catch (Exception ex) { logger.LogWarning(ex, "Batch delete: failed to remove {Path}", p); }
        }
        deleted++;
    }
    logger.LogInformation("Batch deleted {Count} recordings, {Missing} not found", deleted, notFound.Count);
    return Results.Ok(new { deleted, notFound });
});

// 批量打包导出：ids 逗号分隔，流式 ZIP 直写响应（音频已压缩，NoCompression 省时省 CPU）
api.MapGet("/recordings/export/zip", async (HttpContext ctx, string? ids) =>
{
    var idList = (ids ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => long.TryParse(s, out var i) ? i : 0)
        .Where(i => i > 0).Distinct().Take(500).ToList();
    if (idList.Count == 0)
        return Results.BadRequest(new { error = "ids required, e.g. ?ids=1,2,3" });

    var recs = idList.Select(db.GetById)
        .Where(r => r is not null && File.Exists(r!.StoredPath))
        .Select(r => r!)
        .ToList();
    if (recs.Count == 0)
        return Results.NotFound(new { error = "no recordings available" });

    logger.LogInformation("Exporting {Count} recordings as zip", recs.Count);
    ctx.Response.ContentType = "application/zip";
    ctx.Response.Headers.ContentDisposition =
        $"attachment; filename=voicevault-{DateTime.Now:yyyyMMdd-HHmmss}.zip";

    // BodyWriter.AsStream：其同步写内部转 FlushAsync，绕开 Kestrel 禁同步 IO 限制
    // （ZipArchive Dispose 写 central directory 是同步 Write，直接用 Response.Body 会抛异常）
    // entry 名加 id 前缀：天然去重，避免同名文件在 zip 内冲突
    await using var zip = new ZipArchive(
        ctx.Response.BodyWriter.AsStream(leaveOpen: true), ZipArchiveMode.Create, leaveOpen: true);
    foreach (var rec in recs)
    {
        var entry = zip.CreateEntry($"{rec.Id}_{rec.FileName}", CompressionLevel.NoCompression);
        await using var es = entry.Open();
        await using var fs = File.OpenRead(rec.StoredPath);
        await fs.CopyToAsync(es, ctx.RequestAborted);
    }
    await zip.DisposeAsync(); // 显式落 central directory / EOCD 后再结束响应
    return Results.Empty;
});

// ---- 声纹注册（说话人认名）----
// 注册：在录音详情页把某说话人标注成名字 → 从该说话人的全部转写段
// 提取声纹向量入库。之后渲染文字稿时用注册声纹比对每条记录的簇代表
// 声纹，命中即把「说话人N」显示为注册名（历史记录即时生效，无需重转）。

api.MapGet("/speakers", () => Results.Ok(new
{
    items = db.ListSpeakerProfiles().Select(p => new
    {
        id = p.Id,
        name = p.Name,
        createdAt = p.CreatedAt,
        samples = p.Samples.Select(s => new
        {
            recordId = s.RecordId,
            segLabel = s.SegLabel,
            createdAt = s.CreatedAt
        })
    }),
    matchThreshold = cfg.Speaker.MatchThreshold
}));

// 某注册声纹出现在哪些录音：遍历全部已完成转写的簇代表声纹与该档案
// 样本比对，取最高余弦相似度，达阈值即计入。供声纹详情弹窗展示
// 「出现记录」并跳转详情（继续标注/试听）。
api.MapGet("/speakers/{id:long}/occurrences", (long id) =>
{
    var profile = db.ListSpeakerProfiles().FirstOrDefault(p => p.Id == id);
    if (profile is null) return Results.NotFound(new { error = "not found" });
    var samples = profile.Samples.Where(s => s.Embedding.Length > 0).ToList();
    if (samples.Count == 0) return Results.Ok(new { items = Array.Empty<object>() });

    var threshold = cfg.Speaker.MatchThreshold;
    var found = new List<(long RecordedAt, object Item)>();
    foreach (var rec in db.ListForTranscribe("done"))
    {
        if (string.IsNullOrWhiteSpace(rec.TranscriptPath) || !File.Exists(rec.TranscriptPath))
            continue;
        AsrResult? result;
        try
        {
            result = JsonSerializer.Deserialize<AsrResult>(File.ReadAllText(rec.TranscriptPath), jsonOptions);
        }
        catch (Exception) { continue; } // 损坏的文字稿跳过，不中断整表

        (string Label, double Score) best = ("", 0);
        foreach (var spk in result?.Speakers ?? [])
        {
            if (spk.Embedding is not { Count: > 0 }) continue;
            var score = samples.Max(s => VoiceMatcher.Cosine(spk.Embedding, s.Embedding));
            if (score > best.Score) best = (spk.Label ?? "", score);
        }
        if (best.Score >= threshold)
            found.Add((rec.RecordedAt, new
            {
                recordId = rec.Id,
                title = rec.Title,
                recordedAt = rec.RecordedAt,
                label = best.Label,
                score = Math.Round(best.Score, 3)
            }));
    }
    return Results.Ok(new
    {
        items = found.OrderByDescending(f => f.RecordedAt).Select(f => f.Item).ToList()
    });
});

api.MapPost("/speakers", async (RegisterSpeakerRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { error = "name is required" });
    var name = req.Name.Trim();
    if (name.Length > 50)
        return Results.BadRequest(new { error = "name too long (max 50 chars)" });

    var rec = db.GetById(req.RecordId);
    if (rec is null) return Results.NotFound(new { error = "recording not found" });
    if (rec.TranscriptStatus != "done" || string.IsNullOrWhiteSpace(rec.TranscriptPath)
        || !File.Exists(rec.TranscriptPath))
        return Results.BadRequest(new { error = "transcript not ready" });

    // 该说话人标签的全部转写段 → 时间段（边车把多段拼接提取，比单段更稳）
    AsrResult? result;
    try
    {
        result = JsonSerializer.Deserialize<AsrResult>(File.ReadAllText(rec.TranscriptPath), jsonOptions);
    }
    catch (Exception)
    {
        return Results.BadRequest(new { error = "transcript file corrupted" });
    }
    var ranges = (result?.Segments ?? [])
        .Where(s => s.Speaker == req.SpeakerLabel && s.End > s.Start)
        .Select(s => new[] { s.Start, s.End } as double[]).ToList();
    if (ranges.Count == 0)
        return Results.NotFound(new { error = "speaker not found in transcript" });

    try
    {
        var (emb, secs) = await asr.SpeakerEmbeddingAsync(rec.StoredPath, ranges);
        var profileId = db.AddSpeakerSample(name, req.RecordId, req.SpeakerLabel, emb);
        var n = db.ListSpeakerProfiles().First(p => p.Id == profileId).Samples.Count;
        logger.LogInformation(
            "Speaker enrolled: {Name} <- record {Id} {Label} ({Sec:F1}s audio, {Samples} sample(s))",
            name, req.RecordId, req.SpeakerLabel, secs, n);
        return Results.Ok(new { id = profileId, name, seconds = Math.Round(secs, 1), samples = n });
    }
    catch (HttpRequestException ex)
    {
        // 边车 503（声纹模型未就绪）/ 400（有效音频过短）：错误信息透传前端
        return Results.BadRequest(new { error = $"声纹提取失败：{ex.Message}" });
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        logger.LogError(ex, "Speaker enrollment failed");
        return Results.Problem("声纹提取失败：边车不可达或内部错误");
    }
});

api.MapPatch("/speakers/{id:long}", (long id, RenameSpeakerRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { error = "name is required" });
    if (!db.RenameSpeakerProfile(id, req.Name.Trim()))
        return Results.NotFound(new { error = "not found" });
    return Results.Ok(new { id, name = req.Name.Trim() });
});

api.MapDelete("/speakers/{id:long}", (long id) =>
    db.DeleteSpeakerProfile(id)
        ? Results.Ok(new { deleted = id })
        : Results.NotFound(new { error = "not found" }));

// 设置（ASR / 转写时机 / 保留策略，持久化到 settings.json）
// diarizeReady：边车声纹模型是否就绪（探测缓存 5 分钟）——决定「说话人分段」勾选是否生效
api.MapGet("/settings", async () => Results.Ok(new
{
    tokenHint = cfg.Token.Length > 4 ? cfg.Token[..4] + "****" : "****",
    asr = cfg.Asr,
    retention = cfg.Retention,
    pageSize = cfg.PageSize,
    transcribeSpeed = Math.Round(cfg.TranscribeSpeed, 2),
    diarizeReady = await asr.DiarizeReadyAsync()
}));

api.MapPut("/settings", (StoredSettings incoming) =>
{
    cfg.Asr.Enabled = incoming.Asr.Enabled;
    cfg.Asr.Mode = incoming.Asr.Mode;
    cfg.Asr.IdleStartHour = ClampHour(incoming.Asr.IdleStartHour);
    cfg.Asr.IdleEndHour = ClampHour(incoming.Asr.IdleEndHour);
    cfg.Asr.Endpoint = incoming.Asr.Endpoint;
    cfg.Asr.Model = incoming.Asr.Model;
    cfg.Asr.SpeakerDiarization = incoming.Asr.SpeakerDiarization;

    cfg.Retention.Days = Math.Clamp(incoming.Retention.Days, 0, 36500);
    cfg.Retention.MaxBytes = Math.Max(0, incoming.Retention.MaxBytes);
    cfg.PageSize = Math.Clamp(incoming.PageSize <= 0 ? 20 : incoming.PageSize, 10, 200);

    // 持久化到 settings.json：重启容器配置不丢。
    // TranscribeSpeed 由转写队列按实际耗时自适应维护，这里不接收前端值、只透传当前值。
    try
    {
        settingsStore.Save(new StoredSettings
        {
            Asr = cfg.Asr, Retention = cfg.Retention,
            PageSize = cfg.PageSize, TranscribeSpeed = cfg.TranscribeSpeed
        });
    }
    catch (Exception ex) { logger.LogWarning(ex, "Failed to persist settings"); }

    logger.LogInformation(
        "Settings updated: asr={Enabled}, mode={Mode}, idle={S}-{E}, retentionDays={D}, maxBytes={M}, pageSize={P}",
        cfg.Asr.Enabled, cfg.Asr.Mode, cfg.Asr.IdleStartHour, cfg.Asr.IdleEndHour,
        cfg.Retention.Days, cfg.Retention.MaxBytes, cfg.PageSize);

    // 保存后立即生效：闲时积压巡检 + 保留策略清理各跑一次
    idle.TriggerNow();
    retention.TriggerNow();

    return Results.Ok(new { asr = cfg.Asr, retention = cfg.Retention, pageSize = cfg.PageSize });
});

/// <summary>将小时裁剪到 [0,23] 合法范围。</summary>
static int ClampHour(int h) => Math.Clamp(h, 0, 23);

/// <summary>
/// 上传收尾公共逻辑（单请求 multipart 与分片拼接共用）：
/// 流式落盘算 SHA → 入库去重 → 清理去重孤儿副本 → 按转写时机分流。
/// 成功返回 (id, sha, dedup)；dedup=true 表示该文件 (device+sha) 服务器已有，
/// 本次仅去重命中——调用方不应把返回的 id 当作「新上传」处理（如删除）。
/// 校验失败抛 InvalidDataException，其余异常向上传播。
/// </summary>
async Task<(long Id, string Sha, bool Dedup)> CompleteRecordingAsync(
    Stream content, string relPath, string device, string expectedSha, long mtime)
{
    var (storedPath, recordedAt, actualSha, size) =
        await storage.SaveAsync(content, relPath, expectedSha, mtime);

    // 来源分类：从原始路径自动提取（电话号码 / 微信联系人），可后续手动改
    var (contact, contactKind) = ContactExtractor.Extract(relPath);
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
        Contact = contact,
        ContactKind = contactKind,
        CreatedAt = DateTime.UtcNow
    };
    var (id, isNew) = db.Upsert(rec);
    logger.LogInformation("Uploaded {RelPath} -> id={Id} size={Size} dedup={Dedup}",
        relPath, id, size, !isNew);

    // 去重命中：原记录的落盘路径与转写状态保持原样，本次刚写入的副本成为
    // 孤儿文件，这里清理掉；也不重复入队转写（原记录状态已定）
    if (!isNew)
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
        return (id, actualSha, true);
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

    return (id, actualSha, false);
}

/// <summary>导出 TXT：每段一行「[mm:ss] (说话人)文本」，与网页端复制格式一致。</summary>
static string BuildPlainText(AsrResult r)
{
    if (r.Segments is { Count: > 0 })
    {
        static string Ts(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";
        }
        return string.Join("\n", r.Segments.Select(s =>
        {
            var spk = string.IsNullOrWhiteSpace(s.Speaker) ? "" : $"[{s.Speaker}] ";
            return $"[{Ts(s.Start)}] {spk}{s.Text?.Trim() ?? ""}";
        })) + "\n";
    }
    return (r.Text ?? "") + "\n";
}

/// <summary>导出 SRT：标准字幕格式（含说话人前缀），可直接拖入播放器。</summary>
static string BuildSrt(AsrResult r)
{
    static string Clock(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2},{t.Milliseconds:D3}";
    }
    var sb = new StringBuilder();
    var segs = r.Segments ?? [];
    for (var i = 0; i < segs.Count; i++)
    {
        var s = segs[i];
        var spk = string.IsNullOrWhiteSpace(s.Speaker) ? "" : $"[{s.Speaker}] ";
        sb.AppendLine((i + 1).ToString());
        sb.AppendLine($"{Clock(s.Start)} --> {Clock(Math.Max(s.End, s.Start))}");
        sb.AppendLine(spk + (s.Text?.Trim() ?? ""));
        sb.AppendLine();
    }
    return sb.ToString();
}

/// <summary>导出文件名清洗：去掉路径非法字符。</summary>
static string SanitizeFileName(string name)
{
    foreach (var c in Path.GetInvalidFileNameChars())
        name = name.Replace(c, '_');
    return string.IsNullOrWhiteSpace(name) ? "transcript" : name.Trim();
}

/// <summary>
/// 认名注入：按当前注册声纹库比对结果里每个说话人簇的代表向量，
/// 命中（相似度达阈值且与次佳拉开差距）则把该簇所有段的「说话人N」
/// 替换为注册名。就地修改；处理完剥离原始向量（响应/导出均不需要）。
/// </summary>
static void ApplySpeakerNames(AsrResult result, DbService db, VaultConfig cfg)
{
    if (result.Speakers is not { Count: > 0 })
        return;
    var index = db.GetSpeakerIndex();
    if (index.Count > 0)
    {
        // 单条记录内一对一全局分配：同录音簇间相似度天然偏高，逐簇独立
        // 匹配会让某档案删除后被"邻居簇"错误认领，故每档案至多认领一簇
        VoiceMatcher.AssignNames(
            result.Speakers, index, cfg.Speaker.MatchThreshold, cfg.Speaker.MatchMargin);
        var nameByLabel = result.Speakers
            .Where(s => !string.IsNullOrEmpty(s.Name) && !string.IsNullOrEmpty(s.Label))
            .GroupBy(s => s.Label).ToDictionary(g => g.Key, g => g.First().Name!);
        if (nameByLabel.Count > 0)
        {
            foreach (var seg in result.Segments ?? [])
            {
                if (!string.IsNullOrEmpty(seg.Speaker) && nameByLabel.TryGetValue(seg.Speaker, out var n))
                    seg.Speaker = n;
            }
        }
    }
    foreach (var spk in result.Speakers)
        spk.Embedding = null; // 原始向量不出服务端（无注册库时同样剥离）
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
