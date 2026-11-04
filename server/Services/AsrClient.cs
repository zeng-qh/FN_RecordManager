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

/// <summary>ASR 边车返回的完整结果。</summary>
public class AsrResult
{
    public string? Text { get; set; }
    public List<AsrSegment>? Segments { get; set; }
    public string? Language { get; set; }
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
        string? Language, List<AsrSegment>? Segments, string? Text, string? Error);

    // 轮询每秒反序列化一次：选项静态缓存，避免重复构建
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Endpoint 未配置时回退到默认地址，避免拼出非法 URL 静默失败。</summary>
    private string BaseEndpoint =>
        (string.IsNullOrWhiteSpace(cfg.Asr.Endpoint) ? "http://localhost:8000" : cfg.Asr.Endpoint).TrimEnd('/');

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
                    Language = status.Language,
                };

            onProgress?.Invoke(status.Progress, status.Partial ?? "");
        }
    }
}
