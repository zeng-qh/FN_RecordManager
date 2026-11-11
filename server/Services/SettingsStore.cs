using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceVault.Server.Services;

/// <summary>settings.json 的持久化结构（网页端可改的全部设置）。</summary>
public class StoredSettings
{
    public AsrConfig Asr { get; set; } = new();
    public RetentionConfig Retention { get; set; } = new();

    /// <summary>列表每页条数（用户偏好，重启不丢）。合法值钳制 10-200。</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>转写速度因子（实时倍率，&gt;0）。由服务端按实际转写耗时自适应维护，前端只读。</summary>
    public double TranscribeSpeed { get; set; } = 4.0;

    /// <summary>手机无线导入（ADB）配置。可空以兼容旧版 settings.json（缺省保留部署默认值）。</summary>
    public AdbImportConfig? AdbImport { get; set; }
}

/// <summary>
/// 网页端设置的持久化（存 DataDir/settings.json）。
/// 环境变量 / appsettings 提供"部署默认值"，网页端保存后写入本文件，
/// 重启容器配置不丢；启动时若本文件存在，则以其中配置覆盖默认值
/// （即网页端设置对 ASR / 保留策略部分拥有最终解释权）。
/// 兼容旧版仅含 ASR 字段的 settings.json（顶层无 asr 键视为旧格式）。
/// </summary>
public class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dataDir;
    private readonly string _path;
    private readonly object _lock = new();

    public SettingsStore(string dataDir)
    {
        _dataDir = dataDir;
        _path = Path.Combine(dataDir, "settings.json");
    }

    /// <summary>读取已保存设置；文件不存在或损坏时返回 null（用部署默认值）。自动识别旧版纯 ASR 格式。</summary>
    public StoredSettings? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var json = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(json);

            // 旧格式：顶层就是 AsrConfig 字段（无 asr 键）→ 包装兼容
            if (!doc.RootElement.TryGetProperty("asr", out _))
            {
                var oldAsr = JsonSerializer.Deserialize<AsrConfig>(json, JsonOpts);
                return oldAsr is null ? null : new StoredSettings { Asr = oldAsr };
            }

            return JsonSerializer.Deserialize<StoredSettings>(json, JsonOpts);
        }
        catch (Exception)
        {
            // 配置文件损坏不应阻断启动，退回部署默认值
            return null;
        }
    }

    /// <summary>原子保存：先写临时文件再替换，避免写一半断电导致 JSON 损坏。</summary>
    public void Save(StoredSettings settings)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_dataDir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
