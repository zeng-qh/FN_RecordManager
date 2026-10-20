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
/// 仅当 <see cref="AsrConfig.Enabled"/> 为 true 时由服务端触发。
/// </summary>
public class AsrClient(HttpClient http, VaultConfig cfg)
{
    public async Task<AsrResult?> TranscribeAsync(string audioPath, CancellationToken ct = default)
    {
        // 流式读取音频文件，避免把整段录音读入内存（长录音在树莓派上尤为关键）
        using var fileStream = File.OpenRead(audioPath);
        using var content = new MultipartFormDataContent();
        content.Add(new StreamContent(fileStream), "file", Path.GetFileName(audioPath));
        content.Add(new StringContent(cfg.Asr.Model), "model");
        content.Add(new StringContent(cfg.Asr.SpeakerDiarization.ToString()), "diarize");

        var endpoint = cfg.Asr.Endpoint.TrimEnd('/') + "/transcribe";
        using var resp = await http.PostAsync(endpoint, content, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<AsrResult>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
