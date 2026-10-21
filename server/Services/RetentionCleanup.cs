namespace VoiceVault.Server.Services;

/// <summary>
/// 保留策略清理：按「保留天数」删超期录音，按「容量上限」从最旧删至达标。
/// 两项均为 0 时不启用。每小时巡检一次，保存设置后可经 TriggerNow 立即执行。
/// </summary>
public class RetentionCleanup : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

    private readonly DbService _db;
    private readonly VaultConfig _cfg;
    private readonly ILogger<RetentionCleanup> _logger;
    private Timer? _timer;
    private int _running;

    public RetentionCleanup(DbService db, VaultConfig cfg, ILogger<RetentionCleanup> logger)
    {
        _db = db;
        _cfg = cfg;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Retention cleanup started (days={Days}, maxBytes={Max})",
            _cfg.Retention.Days, _cfg.Retention.MaxBytes);
        _timer = new Timer(_ => _ = TickAsync(stoppingToken), null, TimeSpan.Zero, TickInterval);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Retention cleanup stopping…");
        _timer?.Change(Timeout.Infinite, 0);
        _timer?.Dispose();
        await base.StopAsync(cancellationToken);
    }

    /// <summary>外部（如保存设置后）要求立即巡检一次；内部仍受重入保护约束。</summary>
    public void TriggerNow() => _ = Task.Run(() => TickAsync(CancellationToken.None));

    private async Task TickAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            await RunOnceAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention cleanup tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var (days, maxBytes) = (_cfg.Retention.Days, _cfg.Retention.MaxBytes);
        if (days <= 0 && maxBytes <= 0) return;

        var removed = 0;

        // 1) 按天数：删录音时间早于 cutoff 的
        if (days > 0)
        {
            var cutoff = DateTimeOffset.Now.AddDays(-days).ToUnixTimeSeconds();
            foreach (var id in _db.GetIdsOlderThan(cutoff))
            {
                if (ct.IsCancellationRequested) break;
                if (RemoveRecording(id)) removed++;
            }
        }

        // 2) 按容量：从最旧逐条删，直到总量 ≤ 上限
        if (maxBytes > 0)
        {
            while (!ct.IsCancellationRequested && _db.TotalBytes() > maxBytes)
            {
                var oldest = _db.GetOldestId();
                if (oldest is null) break;
                if (!RemoveRecording(oldest.Value)) break; // 删除失败避免死循环
                removed++;
            }
        }

        if (removed > 0)
            _logger.LogInformation("Retention cleanup removed {Count} recordings (days={Days}, maxBytes={Max})",
                removed, days, maxBytes);
        await Task.CompletedTask;
    }

    /// <summary>删除一条录音（元数据 + 落盘文件 + 文字稿 JSON）。返回是否删除成功。</summary>
    private bool RemoveRecording(long id)
    {
        var rec = _db.DeleteById(id);
        if (rec is null) return false;

        foreach (var p in new[] { rec.StoredPath, rec.TranscriptPath })
        {
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
            try { File.Delete(p); }
            catch (Exception ex) { _logger.LogWarning(ex, "Retention cleanup: failed to delete {Path}", p); }
        }
        _logger.LogInformation("Retention removed id={Id} relPath={RelPath}", id, rec.RelPath);
        return true;
    }
}
