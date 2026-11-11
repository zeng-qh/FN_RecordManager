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
    /// <summary>是否启用转写。默认开启（配合默认实时转写：上传即转写）。
    /// 没部署边车时转写会失败重试后标记错误，备份本身不受影响；
    /// 不需要自动转写可在设置页或 compose 里关掉。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>转写触发模式：Off 关闭 / Realtime 实时 / Idle 闲时。默认实时。</summary>
    public TranscribeMode Mode { get; set; } = TranscribeMode.Realtime;

    /// <summary>闲时时段起始小时（0–23，服务器本地时区），含该整点。</summary>
    public int IdleStartHour { get; set; } = 22;

    /// <summary>闲时时段结束小时（0–23，服务器本地时区），不含该整点；可小于起始小时以跨午夜。</summary>
    public int IdleEndHour { get; set; } = 7;

    /// <summary>ASR 边车地址，如 http://localhost:8000。</summary>
    public string Endpoint { get; set; } = "http://localhost:8000";

    /// <summary>使用的 Whisper 模型规模，如 tiny/small/medium。</summary>
    public string Model { get; set; } = "small";

    /// <summary>是否启用说话人分段。默认开启（边车未带声纹模型时优雅降级为不标注）。</summary>
    public bool SpeakerDiarization { get; set; } = true;

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
/// 保留策略：按天数 / 容量上限自动清理最旧录音。两项均为 0 时不启用。
/// </summary>
public class RetentionConfig
{
    /// <summary>录音保留天数（按录音时间计），超过即删；0 = 永久保留。</summary>
    public int Days { get; set; }

    /// <summary>存储容量上限（字节），超出后从最旧录音开始删除直至达标；0 = 不限制。</summary>
    public long MaxBytes { get; set; }
}

/// <summary>
/// 声纹注册（说话人认名）配置。可用环境变量覆盖：
/// VAULT_Speaker__MatchThreshold / VAULT_Speaker__MatchMargin。
/// </summary>
public class SpeakerConfig
{
    /// <summary>
    /// 认定「同一人」的最低余弦相似度。实测校准（演示库 CAM++，电话信道）：
    /// 同一次转写内注册簇自匹配 0.78–0.98；跨录音的异人相似度可到 0.74
    /// （信道压缩把所有人的频谱拉近，同异分数在 0.55–0.74 重叠，其中
    /// 连「同人跨录音」也只有 0.59 左右）——0.75 以下必然出现跨录音误认，
    /// 故默认取 0.75（宁显匿名不认错人）。宽带同设备场景可酌情调低。
    /// </summary>
    public double MatchThreshold { get; set; } = 0.75;

    /// <summary>与次佳档案的最小差距：最佳与次佳相差不足该值视为歧义，拒绝认名。</summary>
    public double MatchMargin { get; set; } = 0.05;
}

/// <summary>
/// 手机无线导入（ADB over WiFi）配置。
/// 手机开启「无线调试」后，服务端定时经 adb 从手机拉取录音目录自动入库：
/// 复用上传公共逻辑（SHA-256 去重、来源分类、按转写时机分流），
/// 增量依据 manifest（remote 路径 + 大小），已入库/不支持的文件不会重复拉取。
/// </summary>
public class AdbImportConfig
{
    /// <summary>是否启用手机无线导入。默认关闭。</summary>
    public bool Enabled { get; set; }

    /// <summary>扫描间隔（分钟）。服务端钳制到 5–1440。</summary>
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>
    /// 手机地址列表，逗号分隔。推荐填「无线调试」页显示的 IP:端口（端口随开关漂移，
    /// 变了需更新）；只填 IP 时按默认端口 5555 连接，并尽力通过 mDNS 自动探测端口。
    /// </summary>
    public string Hosts { get; set; } = "";

    /// <summary>
    /// 手机端要拉取的录音目录，逗号分隔。如微信语音
    /// /sdcard/Android/data/com.tencent.mm/MicroMsg/&lt;账号hash&gt;/voice2
    /// （具体路径按手机实际为准）。
    /// </summary>
    public string RemoteDirs { get; set; } = "";

    /// <summary>adb 可执行文件路径。容器镜像内置 /usr/bin/adb；测试环境可指向 stub。</summary>
    public string AdbPath { get; set; } = "adb";
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

    public RetentionConfig Retention { get; set; } = new();

    public SpeakerConfig Speaker { get; set; } = new();

    /// <summary>手机无线导入（ADB over WiFi）配置，网页端可开关。</summary>
    public AdbImportConfig AdbImport { get; set; } = new();

    /// <summary>列表每页条数（用户偏好，网页端设置持久化到 settings.json）。</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>
    /// 转写速度因子（实时倍率）：1 秒音频实际转写耗时 = 1 / 该值 秒。
    /// 初始为经验默认值；每完成一次转写按实际耗时 EMA 自适应修正（见 TranscriptionQueue），
    /// 持久化到 settings.json，供「转写预计耗时」估算。
    /// </summary>
    public double TranscribeSpeed { get; set; } = 4.0;
}
