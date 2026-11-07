using System.Text.Json;

namespace VoiceVault.Server.Services;

/// <summary>转写结果中的一段（含可选说话人）。</summary>
public class AsrSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public string? Text { get; set; }
    public string? Speaker { get; set; }
}

/// <summary>
/// 转写结果中的一个说话人簇：label 与边车聚类编号（说话人N）对应，
/// Embedding 为该簇代表声纹（192 维，均值后 L2 归一化）；Name 由主服务
/// 比对声纹注册库后动态注入（转写文件本身始终存边车原始输出）。
/// </summary>
public class AsrSpeakerInfo
{
    public string Label { get; set; } = "";
    public List<float>? Embedding { get; set; }
    public string? Name { get; set; }
}

/// <summary>ASR 边车返回的完整结果。</summary>
public class AsrResult
{
    public string? Text { get; set; }
    public List<AsrSegment>? Segments { get; set; }
    public List<AsrSpeakerInfo>? Speakers { get; set; }
    public string? Language { get; set; }

    /// <summary>
    /// 音频容器真实时长（秒，含尾部静音）。空/0 表示边车未能读取，
    /// 调用方回退为「最后一段语音的结束时间」。
    /// </summary>
    public double? Duration { get; set; }

    /// <summary>有声音时长（秒，VAD 过滤静音后的语音时长）。空/0 表示边车未返回。</summary>
    public double? Speech { get; set; }
}

/// <summary>
/// 调用本地 ASR 边车（Python + faster-whisper）的最小客户端。
/// 采用「提交任务 + 轮询进度」模式：边车后台推理，每轮询一次回报
/// 进度百分比与已转出的部分文本，实现"转一部分显示一部分"。
/// </summary>
public class AsrClient(HttpClient http, VaultConfig cfg)
{
    /// <summary>边车 /transcribe-status 返回的任务状态。</summary>
    private record JobStatus(
        string? Status, double Progress, string? Partial,
        string? Language, List<AsrSegment>? Segments, List<AsrSpeakerInfo>? Speakers,
        string? Text, double? Duration, double? Speech, string? Error);

    // 轮询每秒反序列化一次：选项静态缓存，避免重复构建
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>边车 /health 的返回体（diarize=声纹模型是否就绪）。</summary>
    private record HealthInfo(bool Diarize);

    /// <summary>声纹就绪探测缓存（静态：跨实例共享），5 分钟内不重复探测。</summary>
    private static (bool Ready, DateTimeOffset At) _diarizeCache;

    /// <summary>Endpoint 未配置时回退到默认地址，避免拼出非法 URL 静默失败。</summary>
    private string BaseEndpoint =>
        (string.IsNullOrWhiteSpace(cfg.Asr.Endpoint) ? "http://localhost:8000" : cfg.Asr.Endpoint).TrimEnd('/');

    /// <summary>
    /// 探测边车是否在线（GET /health，2 秒超时）。
    /// 供转写队列在边车重启/被回收期间挂起等待，避免把任务误判为失败。
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var resp = await http.GetAsync(BaseEndpoint + "/health", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 队列停机等真实取消向上传播
        }
        catch
        {
            return false; // 连接拒绝/超时 = 边车不在线
        }
    }

    /// <summary>
    /// 探测边车声纹模型是否就绪（/health 的 diarize 字段）。
    /// 结果缓存 5 分钟：设置页每次打开都会读，避免高频探测；
    /// 探测失败沿用上次结果（边车临时不可达时 UI 不闪烁）。
    /// </summary>
    public async Task<bool> DiarizeReadyAsync(CancellationToken ct = default)
    {
        var (ready, at) = _diarizeCache;
        if (DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(5))
            return ready;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var d = await http.GetFromJsonAsync<HealthInfo>(BaseEndpoint + "/health", JsonOpts, cts.Token);
            _diarizeCache = (d?.Diarize == true, DateTimeOffset.UtcNow);
            return _diarizeCache.Ready;
        }
        catch
        {
            return ready; // 边车不在线：沿用缓存值
        }
    }

    /// <summary>
    /// 声纹注册：把录音指定时间段发给边车提取 192 维声纹向量。
    /// 多段时间戳（秒）由调用方从文字稿按说话人标签聚合而来。
    /// 边车模型未就绪（503）或有效音频过短（400）时抛 HttpRequestException，
    /// 调用方透传其错误信息给前端。
    /// </summary>
    public async Task<(float[] Embedding, double Seconds)> SpeakerEmbeddingAsync(
        string audioPath, List<double[]> ranges, CancellationToken ct = default)
    {
        using var fileStream = File.OpenRead(audioPath);
        using var content = new MultipartFormDataContent();
        content.Add(new StreamContent(fileStream), "file", Path.GetFileName(audioPath));
        content.Add(new StringContent(
            JsonSerializer.Serialize(ranges, JsonOpts)), "ranges");

        using var resp = await http.PostAsync(BaseEndpoint + "/speaker-embedding", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(body);
        var parsed = JsonSerializer.Deserialize<EmbeddingResponse>(body, JsonOpts);
        if (parsed?.Embedding is not { Length: > 0 } emb)
            throw new InvalidOperationException("asr sidecar returned empty embedding");
        return (emb, parsed.Seconds);
    }

    private record EmbeddingResponse(float[] Embedding, double Seconds);

    /// <summary>
    /// 提交异步转写任务并轮询至完成。
    /// <paramref name="taskId"/> 用录音 id（同任务重复提交即重启）；
    /// <paramref name="onProgress"/> 每次轮询回调（progress 0-100，partial 已转文本）。
    /// </summary>
    public async Task<AsrResult?> TranscribeAsync(
        long taskId, string audioPath,
        Action<double, string>? onProgress = null,
        CancellationToken ct = default)
    {
        // 流式读取音频文件，避免把整段录音读入内存（长录音在树莓派上尤为关键）
        using (var fileStream = File.OpenRead(audioPath))
        using (var content = new MultipartFormDataContent())
        {
            content.Add(new StreamContent(fileStream), "file", Path.GetFileName(audioPath));
            content.Add(new StringContent(cfg.Asr.Model), "model");
            content.Add(new StringContent(cfg.Asr.SpeakerDiarization.ToString()), "diarize");
            content.Add(new StringContent(taskId.ToString()), "task_id");

            using var resp = await http.PostAsync(BaseEndpoint + "/transcribe-async", content, ct);
            resp.EnsureSuccessStatusCode();
        }

        // 轮询：1s 间隔，直到 done / error
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct);

            var status = await http.GetFromJsonAsync<JobStatus>(
                $"{BaseEndpoint}/transcribe-status/{taskId}", JsonOpts, ct);
            if (status is null)
                throw new InvalidOperationException("asr status empty");

            if (status.Status == "error")
                throw new InvalidOperationException("asr job failed: " + (status.Error ?? "unknown"));

            if (status.Status == "done")
                return new AsrResult
                {
                    Text = status.Text,
                    Segments = status.Segments,
                    Speakers = status.Speakers,
                    Language = status.Language,
                    Duration = status.Duration,
                    Speech = status.Speech,
                };

            onProgress?.Invoke(status.Progress, status.Partial ?? "");
        }
    }
}
