using System.Diagnostics;
using System.Text.Json;

namespace VoiceVault.Server.Services;

/// <summary>
/// 手机无线导入（ADB over WiFi）：
/// 手机开启「无线调试」后，本服务按设定间隔经 adb 连接手机，
/// 扫描配置的录音目录，把新增音频文件拉到暂存区并直接走上传公共逻辑入库
/// （SHA-256 去重、来源分类、按转写时机分流，与网页/客户端上传完全一致）。
///
/// 增量依据 manifest（serial + 手机端路径 + 大小）：已入库、去重命中、
/// 明确不支持的文件类型都不会重复拉取；拉取失败不记 manifest，下一轮自动重试。
///
/// 连接策略：配置「IP:端口」最稳（无线调试端口会随开关漂移）；只配 IP 时
/// 先尝试 mDNS 自动发现端口，失败退回 5555（bridge 容器常收不到组播，尽力而为）。
/// </summary>
public class AdbImportService : BackgroundService
{
    /// <summary>
    /// 入库委托：由 Program.cs 在构建完成后注入上传收尾公共逻辑
    /// CompleteRecordingAsync（落盘算 SHA → 去重 → 入库 → 转写分流）。
    /// expectedSha 传空（手机端无法预先算哈希），服务端落盘时自行计算真实 SHA。
    /// </summary>
    public delegate Task<(long Id, string Sha, bool Dedup)> IngestDelegate(
        Stream content, string relPath, string device, string expectedSha, long mtime);

    /// <summary>入库入口（服务构建后由 Program.cs 注入；未注入时导入轮次跳过）。</summary>
    public IngestDelegate? Ingest { get; set; }

    private readonly VaultConfig _cfg;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _trigger = new(0);
    private readonly string _stagingDir;
    private readonly string _manifestPath;
    private Dictionary<string, ManifestEntry> _manifest = new(StringComparer.OrdinalIgnoreCase);
    private bool _manifestDirty;

    public AdbImportService(VaultConfig cfg, ILogger<AdbImportService> logger)
    {
        _cfg = cfg;
        _logger = logger;
        _stagingDir = Path.Combine(cfg.DataDir, "adb-import", "staging");
        _manifestPath = Path.Combine(cfg.DataDir, "adb-import", "manifest.json");
        Directory.CreateDirectory(_stagingDir); // pull 的落点，必须先于首轮创建
        LoadManifest();
    }

    /// <summary>设置保存后立即触发一轮扫描（开关打开时马上试跑，不必等下一个间隔）。</summary>
    public void TriggerNow()
    {
        if (_trigger.CurrentCount == 0) _trigger.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 启动缓 15 秒：等 Web 主机就绪、NAS 网络稳定后再做首轮扫描
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var a = _cfg.AdbImport;
            try
            {
                if (a.Enabled) await RunOnce(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ADB import round failed");
            }

            // 定时等待；TriggerNow（设置保存）可提前唤醒进入下一轮
            var minutes = Math.Clamp(a.IntervalMinutes <= 0 ? 30 : a.IntervalMinutes, 1, 1440);
            try
            {
                await Task.WhenAny(
                    Task.Delay(TimeSpan.FromMinutes(minutes), ct),
                    _trigger.WaitAsync(ct));
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        var a = _cfg.AdbImport;
        var adb = string.IsNullOrWhiteSpace(a.AdbPath) ? "adb" : a.AdbPath.Trim();

        if (Ingest is null)
        {
            _logger.LogWarning("ADB import enabled but ingest pipeline not wired, skip");
            return;
        }
        var ingest = Ingest;

        var hosts = SplitList(a.Hosts);
        var dirs = SplitList(a.RemoteDirs);
        if (hosts.Count == 0 || dirs.Count == 0)
        {
            _logger.LogInformation("ADB import enabled but 手机地址/录音目录未配置，本轮跳过");
            return;
        }

        // adb 可用性探测（容器镜像需含 android-tools）
        var version = await RunAdbAsync(adb, new[] { "version" }, 10, ct);
        if (version is null || !version.Contains("Android Debug Bridge"))
        {
            _logger.LogWarning(
                "ADB import 启用但 '{AdbPath}' 不可用（部署环境需安装 adb：Debian 装 adb / Alpine 装 android-tools）", adb);
            return;
        }

        var mdnsPorts = await DiscoverMdnsPorts(adb, ct);
        foreach (var host in hosts)
        {
            ct.ThrowIfCancellationRequested();
            try { await ImportFromHost(adb, host, dirs, mdnsPorts, ingest, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ADB import failed for {Host}", host);
            }
        }
        if (_manifestDirty) SaveManifest();
    }

    private async Task ImportFromHost(string adb, string host, List<string> dirs,
        Dictionary<string, int> mdnsPorts, IngestDelegate ingest, CancellationToken ct)
    {
        var (ip, explicitPort) = ParseHost(host);
        // 优先级：配置里的显式端口 > mDNS 探测 > 默认 5555
        // （注意不能把配置端口传给 TryGetValue 的 out 参数——未命中会被覆盖成 0）
        var port = explicitPort != 0
            ? explicitPort
            : mdnsPorts.TryGetValue(ip, out var discovered) ? discovered : 5555;
        var serial = $"{ip}:{port}";

        var connectOut = await RunAdbAsync(adb, new[] { "connect", serial }, 15, ct);
        if (connectOut is null || !connectOut.Contains("connected", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("ADB connect {Serial} 失败：{Out}", serial, (connectOut ?? "").Trim());
            return;
        }

        var devices = await RunAdbAsync(adb, new[] { "devices" }, 10, ct) ?? "";
        var stateLine = devices.Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith(serial, StringComparison.Ordinal));
        var state = stateLine?.Split('\t', StringSplitOptions.RemoveEmptyEntries)
            .ElementAtOrDefault(1)?.Trim();
        if (state != "device")
        {
            if (state == "unauthorized")
                _logger.LogWarning(
                    "ADB {Serial} 未授权：请在手机上点亮屏幕并确认「允许无线调试？」授权弹窗", serial);
            else
                _logger.LogWarning("ADB {Serial} 未就绪（状态：{State}）", serial, state ?? "offline");
            return;
        }

        var deviceTag = $"adb-{ip.Replace('.', '_')}";
        var imported = 0;
        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            if (!SafeShellArg(dir))
            {
                _logger.LogWarning("ADB import：录音目录含非法字符已跳过：{Dir}", dir);
                continue;
            }

            var entries = await ListRemoteFiles(adb, serial, dir, ct);
            foreach (var (path, size, mtime) in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!SafeShellArg(path)) continue;

                var key = $"{serial}|{path}";
                if (_manifest.TryGetValue(key, out var done) && done.Size == size)
                    continue; // 已处理过且手机端未变化

                var fileName = Path.GetFileName(path);
                if (!StorageService.IsSupportedAudio(fileName))
                {
                    // 非音频（索引/临时/缓存文件）标记后不再重复拉取
                    MarkManifest(key, size, mtime, "unsupported");
                    continue;
                }

                var local = Path.Combine(_stagingDir,
                    Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
                var pullOut = await RunAdbAsync(adb,
                    new[] { "-s", serial, "pull", path, local }, 900, ct);
                if (pullOut is null || !File.Exists(local))
                {
                    _logger.LogWarning("ADB pull 失败 {Path}：{Out}", path, (pullOut ?? "").Trim());
                    continue; // 不记 manifest，下一轮自动重试
                }

                try
                {
                    await using var fs = new FileStream(
                        local, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var (id, sha, dedup) = await ingest(fs, path, deviceTag, "", mtime);
                    MarkManifest(key, size, mtime, "imported");
                    SaveManifest(); // 逐条落盘：中途断电最多重复一个文件（SHA 去重兜底）
                    imported++;
                    _logger.LogInformation("ADB 导入 {Path} -> id={Id} dedup={Dedup}", path, id, dedup);
                }
                catch (InvalidDataException ex)
                {
                    _logger.LogWarning(ex, "ADB 导入被拒绝 {Path}", path);
                    MarkManifest(key, size, mtime, "unsupported");
                }
                finally
                {
                    try { if (File.Exists(local)) File.Delete(local); }
                    catch { /* 暂存清理失败不影响主流程 */ }
                }
            }
        }
        if (imported > 0)
            _logger.LogInformation("ADB import {Serial}：本轮新入库 {Count} 条录音", serial, imported);
    }

    /// <summary>
    /// 列出手机端目录下全部文件（路径 + 大小 + 修改时间）。
    /// 优先用 find+stat 一次拿全（Android 10+ toybox 支持）；
    /// 设备不支持 stat -c 时降级为纯 find（size/mtime 记 0，仍可按路径增量）。
    /// </summary>
    private async Task<List<(string Path, long Size, long Mtime)>> ListRemoteFiles(
        string adb, string serial, string dir, CancellationToken ct)
    {
        var result = new List<(string, long, long)>();
        var listing = await RunAdbAsync(adb,
            new[] { "-s", serial, "shell", $"find '{dir}' -type f -exec stat -c '%s|%Y|%n' {{}} +" },
            120, ct) ?? "";
        foreach (var line in listing.Split('\n'))
            if (ParseStatLine(line) is { } e && e.Path is not null)
                result.Add(e);

        if (result.Count == 0 && listing.Trim().Length > 0)
        {
            // 降级：stat 不可用，仅按路径枚举
            var plain = await RunAdbAsync(adb,
                new[] { "-s", serial, "shell", $"find '{dir}' -type f" }, 120, ct) ?? "";
            foreach (var line in plain.Split('\n'))
            {
                var p = line.Trim().TrimEnd('\r');
                if (p.Length > 0 && p.StartsWith(dir, StringComparison.Ordinal)) result.Add((p, 0, 0));
            }
        }
        return result;
    }

    /// <summary>尽力而为：经 mDNS 自动发现同网段开启无线调试的手机端口
    /// （adb mdns services；bridge 容器网络常收不到组播，探测不到就走配置端口/5555）。</summary>
    private async Task<Dictionary<string, int>> DiscoverMdnsPorts(string adb, CancellationToken ct)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var outp = await RunAdbAsync(adb, new[] { "mdns", "services" }, 8, ct);
        if (string.IsNullOrEmpty(outp)) return map;
        foreach (var line in outp.Split('\n'))
        {
            var addr = line.Trim().Split('\t').LastOrDefault(s => s.Contains(':'));
            if (addr is null) continue;
            var (ip, port) = ParseHost(addr);
            if (port > 0 && ip.Contains('.')) map[ip] = port;
        }
        return map;
    }

    /// <summary>运行 adb 子进程，返回合并输出；启动失败/超时返回 null，取消向上传播。</summary>
    private static async Task<string?> RunAdbAsync(
        string adb, string[] args, int timeoutSec, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(adb)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a); // 参数数组传递，杜绝本地注入
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            var exitTask = proc.WaitForExitAsync(ct);
            var finished = await Task.WhenAny(exitTask, Task.Delay(timeoutSec * 1000, CancellationToken.None));
            if (finished != exitTask)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                return null; // 超时
            }
            await exitTask; // 传播取消
            return (await stdoutTask) + (await stderrTask);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; } // adb 不存在等环境问题 → 视为不可用
    }

    private static (string? Path, long Size, long Mtime) ParseStatLine(string line)
    {
        var s = line.Trim().TrimEnd('\r');
        var parts = s.Split('|');
        if (parts.Length < 3 || !long.TryParse(parts[0].Trim(), out var size)) return (null, 0, 0);
        long.TryParse(parts[1].Trim(), out var mtime);
        var path = string.Join('|', parts.Skip(2)).Trim();
        return (path.Length > 0 ? path : null, size, mtime);
    }

    private static (string Ip, int Port) ParseHost(string host)
    {
        var s = host.Trim();
        var i = s.LastIndexOf(':');
        if (i > 0 && int.TryParse(s[(i + 1)..], out var p) && p is > 0 and <= 65535)
            return (s[..i], p);
        return (s, 0);
    }

    /// <summary>手机端 shell 参数安全校验（防注入）：禁 shell 元字符，允许空格/中文等普通字符。</summary>
    private static bool SafeShellArg(string s) =>
        !string.IsNullOrWhiteSpace(s) && s.Length < 500 &&
        s.IndexOfAny(['"', '\'', ';', '|', '&', '$', '`', '<', '>', '\\', '\r', '\n']) < 0;

    private static List<string> SplitList(string? s) =>
        (s ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .Distinct()
            .ToList();

    private void MarkManifest(string key, long size, long mtime, string status)
    {
        _manifest[key] = new ManifestEntry
        {
            Size = size,
            Mtime = mtime,
            Status = status,
            ImportedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        _manifestDirty = true;
    }

    private void LoadManifest()
    {
        try
        {
            if (!File.Exists(_manifestPath)) return;
            var json = File.ReadAllText(_manifestPath);
            _manifest = JsonSerializer.Deserialize<Dictionary<string, ManifestEntry>>(json)
                ?? new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // manifest 损坏则重建：代价是部分文件重新拉取，SHA-256 去重保证不重复占空间
            _manifest = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveManifest()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);
            var tmp = _manifestPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_manifest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            File.Move(tmp, _manifestPath, overwrite: true);
            _manifestDirty = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save adb import manifest");
        }
    }

    private sealed class ManifestEntry
    {
        public long Size { get; set; }
        public long Mtime { get; set; }

        /// <summary>imported 已入库 / unsupported 非音频或被拒（不再重试）。</summary>
        public string Status { get; set; } = "imported";

        public long ImportedAt { get; set; }
    }
}
