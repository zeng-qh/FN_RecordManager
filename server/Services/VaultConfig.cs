namespace VoiceVault.Server.Services;

/// <summary>
/// 本地语音转写（ASR）边车配置。
/// </summary>
public class AsrConfig
{
    /// <summary>是否启用转写。默认关闭，备份不受影响。</summary>
    public bool Enabled { get; set; }

    /// <summary>ASR 边车地址，如 http://localhost:8000。</summary>
    public string Endpoint { get; set; } = "http://localhost:8000";

    /// <summary>使用的 Whisper 模型规模，如 tiny/small/medium。</summary>
    public string Model { get; set; } = "small";

    /// <summary>是否启用说话人分段（进阶，需边车支持）。</summary>
    public bool SpeakerDiarization { get; set; }
}

/// <summary>
/// 服务端运行配置，绑定自 appsettings.json 与环境变量（VAULT_ 前缀，嵌套用 __）。
/// 例：VAULT_TOKEN / VAULT_DATADIR / VAULT_PORT / VAULT_ASR__ENABLED。
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
