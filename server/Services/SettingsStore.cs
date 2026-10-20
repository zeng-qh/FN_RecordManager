using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceVault.Server.Services;

/// <summary>
/// 网页端 ASR 设置的持久化（存 DataDir/settings.json）。
/// 环境变量 / appsettings 提供"部署默认值"，网页端保存后写入本文件，
/// 重启容器配置不丢；启动时若本文件存在，则以其中 ASR 配置覆盖默认值
/// （即网页端设置对 ASR 部分拥有最终解释权）。
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

    /// <summary>读取已保存的 ASR 设置；文件不存在或损坏时返回 null（用部署默认值）。</summary>
    public AsrConfig? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AsrConfig>(json, JsonOpts);
        }
        catch (Exception)
        {
            // 配置文件损坏不应阻断启动，退回部署默认值
            return null;
        }
    }

    /// <summary>原子保存：先写临时文件再替换，避免写一半断电导致 JSON 损坏。</summary>
    public void Save(AsrConfig asr)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_dataDir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(asr, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
