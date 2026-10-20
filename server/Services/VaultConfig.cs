using System.Text.Json.Serialization;

namespace VoiceVault.Server.Services;

/// <summary>
/// 转写触发模式。以字符串（而非整数）序列化，便于前端直接匹配。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranscribeMode
{
    /// <summary>不自动转写，仅手动触发。</summary>
    Off,

    /// <summary>实时转写：上传完成后立即进入后台队列。</summary>
    Realtime,

    /// <summary>闲时转写：仅在用户设定的空闲时段内处理待转写队列，平时只做标记。</summary>
    Idle
}

/// <summary>
/// 本地语音转写（ASR）边车配置。
/// </summary>
public class AsrConfig
{
    /// <summary>是否启用转写。默认关闭，备份不受影响。</summary>
    public bool Enabled { get; set; }

    /// <summary>转写触发模式：Off 关闭 / Realtime 实时 / Idle 闲时。</summary>
    public TranscribeMode Mode { get; set; } = TranscribeMode.Off;

    /// <summary>闲时时段起始小时（0–23，服务器本地时区），含该整点。</summary>
    public int IdleStartHour { get; set; } = 22;

    /// <summary>闲时时段结束小时（0–23，服务器本地时区），不含该整点；可小于起始小时以跨午夜。</summary>
    public int IdleEndHour { get; set; } = 7;

    /// <summary>ASR 边车地址，如 http://localhost:8000。</summary>
    public string Endpoint { get; set; } = "http://localhost:8000";

    /// <summary>使用的 Whisper 模型规模，如 tiny/small/medium。</summary>
    public string Model { get; set; } = "small";

    /// <summary>是否启用说话人分段（进阶，需边车支持）。</summary>
    public bool SpeakerDiarization { get; set; }

    /// <summary>判断给定本地时间是否处于本配置定义的闲时窗口内。</summary>
    public bool InIdleWindowNow(DateTime? localNow = null)
    {
        var now = localNow ?? DateTime.Now;
        return IsInIdleWindow(now, IdleStartHour, IdleEndHour);
    }

    /// <summary>纯函数：判断 <paramref name="localNow"/> 是否落在 [startHour, endHour) 区间；
    /// 当 startHour &gt; endHour 时视为跨午夜窗口（如 22→7 表示 22:00 至次日 07:00）。</summary>
    public static bool IsInIdleWindow(DateTime localNow, int startHour, int endHour)
    {
        if (startHour < 0 || startHour > 23 || endHour < 0 || endHour > 23) return false;
        if (startHour == endHour) return false; // 退化配置视为不启用
        var h = localNow.Hour;
        return startHour < endHour
            ? (h >= startHour && h < endHour)
            : (h >= startHour || h < endHour);
    }
}

/// <summary>
/// 服务端运行配置，绑定自 appsettings.json 与环境变量（VAULT_ 前缀，嵌套用 __）。
/// 例：VAULT_TOKEN / VAULT_DATADIR / VAULT_PORT / VAULT_ASR__MODE / VAULT_ASR__IDLESTARTHOUR。
/// </summary>
public class VaultConfig
{
    /// <summary>Bearer Token，客户端与网页端共用。</summary>
    public string Token { get; set; } = "dev-token-change-me";

    /// <summary>数据根目录，录音/元数据/转写均存于此。</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>监听端口。</summary>
    public int Port { get; set; } = 8080;

    /// <summary>监听地址。</summary>
    public string Bind { get; set; } = "0.0.0.0";

    public AsrConfig Asr { get; set; } = new();
}
