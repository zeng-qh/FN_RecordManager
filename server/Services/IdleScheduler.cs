using System.Threading;

namespace VoiceVault.Server.Services;

/// <summary>
/// 闲时转写调度器：仅在「闲时模式」开启且当前处于用户设定的空闲时段内，
/// 把状态为 idle（上传时按闲时模式标记）的录音批量入队，由 TranscriptionQueue 串行转写。
/// 空闲时段之外不启动新任务；已在进行中的转写会自然收尾，不会强行中断。
/// </summary>
public class IdleScheduler : BackgroundService
{
    // 每分钟巡检一次；足够及时，又不频繁访问数据库。
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly DbService _db;
    private readonly TranscriptionQueue _queue;
    private readonly VaultConfig _cfg;
    private readonly ILogger<IdleScheduler> _logger;
    private Timer? _timer;
    private int _running; // 防止上一次 tick 未结束时重入

    public IdleScheduler(
        DbService db, TranscriptionQueue queue, VaultConfig cfg, ILogger<IdleScheduler> logger)
    {
        _db = db;
        _queue = queue;
        _cfg = cfg;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Idle scheduler started (mode={Mode}, window={S}-{E})",
            _cfg.Asr.Mode, _cfg.Asr.IdleStartHour, _cfg.Asr.IdleEndHour);

        // 立即跑一次，便于刚启动且正处于空闲时段时尽快消化积压；之后按间隔巡检。
        _timer = new Timer(_ => _ = TickAsync(stoppingToken), null, TimeSpan.Zero, TickInterval);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Idle scheduler stopping…");
        _timer?.Change(Timeout.Infinite, 0);
        _timer?.Dispose();
        await base.StopAsync(cancellationToken);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // 重入保护：若上一次仍在执行（极端慢查询），跳过本次。
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            await RunOnceAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Idle scheduler tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>外部（如保存设置后）要求立即巡检一次；内部仍受重入保护约束。</summary>
    public void TriggerNow() => _ = Task.Run(() => TickAsync(CancellationToken.None));

    /// <summary>单次巡检：满足条件时把 idle 录音入队。抽出为 internal 便于测试。</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        if (!_cfg.Asr.Enabled || _cfg.Asr.Mode != TranscribeMode.Idle)
            return;

        if (!_cfg.Asr.InIdleWindowNow())
            return;

        var ids = _db.GetIdlePendingIds();
        if (ids.Count == 0)
            return;

        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            // 先置 pending 避免同一次巡检或下一分钟重复入队，再由队列串行处理。
            _db.UpdateTranscript(id, "", "pending", null);
            _queue.Enqueue(id);
        }
        _logger.LogInformation("Idle window: enqueued {Count} recordings for transcription", ids.Count);
    }
}
