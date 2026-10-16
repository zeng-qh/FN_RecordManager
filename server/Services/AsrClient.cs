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
    public async Task<AsrResult?> TranscribeAsync(string audioPath)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(await File.ReadAllBytesAsync(audioPath)),
            "file", Path.GetFileName(audioPath));
        content.Add(new StringContent(cfg.Asr.Model), "model");
        content.Add(new StringContent(cfg.Asr.SpeakerDiarization.ToString()), "diarize");

        var endpoint = cfg.Asr.Endpoint.TrimEnd('/') + "/transcribe";
        using var resp = await http.PostAsync(endpoint, content);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<AsrResult>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
