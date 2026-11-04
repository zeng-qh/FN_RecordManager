using System.Text.Json;
using System.Threading.Channels;
using VoiceVault.Server.Models;

namespace VoiceVault.Server.Services;

/// <summary>
/// 后台转写队列：所有转写任务（手动触发 / 上传后自动触发）统一排队，
/// 逐条串行执行，避免并发打满 NAS 资源。
/// 每次转写记录实际耗时，并按音频时长自适应修正全局速度因子
/// （供「转写预计耗时」估算），持久化到 settings.json。
/// </summary>
public class TranscriptionQueue : BackgroundService
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _cts = new();
    private readonly DbService _db;
    private readonly AsrClient _asr;
    private readonly VaultConfig _cfg;
    private readonly SettingsStore _settings;
    private readonly ILogger<TranscriptionQueue> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public TranscriptionQueue(
        DbService db, AsrClient asr, VaultConfig cfg, SettingsStore settings, ILogger<TranscriptionQueue> logger)
    {
        _db = db;
        _asr = asr;
        _cfg = cfg;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>把一条录音加入转写队列（幂等，队列满/停止时静默丢弃）。</summary>
    public void Enqueue(long id) => _channel.Writer.TryWrite(id);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Transcription queue started (asr enabled={Enabled}, mode={Mode})",
            _cfg.Asr.Enabled, _cfg.Asr.Mode);
        try
        {
            await foreach (var id in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    // 边车离线（重启/被回收）时挂起等待而非判失败：
                    // 否则排队任务会在几十毫秒内连续 Connection refused 全灭
                    await WaitForSidecarAsync(_cts.Token);
                    await TranscribeAsync(id, _cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    // 停机取消：状态已是 pending，无需回滚
                    _logger.LogInformation("Transcribe cancelled id={Id} (shutdown)", id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Transcribe failed id={Id}", id);
                    // 失败计次：闲时窗口内 attempts < 3 时会被自动重试（初失败后最多再试 2 次）
                    _db.UpdateTranscript(id, "", "error", null, attemptsDelta: +1);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机：队列已 Complete，无更多任务
        }
    }

    /// <summary>
    /// 优雅停止：先标记 Channel 完成（排空已入队任务），再取消进行中的转写，
    /// 最后交由基类在 ShutdownTimeout 内等待 ExecuteAsync 退出。
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Transcription queue stopping: completing writer, cancelling in-flight job…");
        _channel.Writer.TryComplete();
        _cts.Cancel();
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// 等待边车就绪：在线则立即返回；离线则每 5 秒探测一次，
    /// 最长等 15 分钟（超时抛异常走正常失败流程，闲时窗口内仍会自动重试）。
    /// 典型场景：边车容器重启（数十秒）、NAS 重启后服务先起边车后起。
    /// </summary>
    private async Task WaitForSidecarAsync(CancellationToken ct)
    {
        if (await _asr.IsHealthyAsync(ct)) return;

        _logger.LogWarning("ASR sidecar offline — transcription queue paused, waiting for recovery…");
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (!await _asr.IsHealthyAsync(ct))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("ASR sidecar offline for over 15 minutes");
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { throw; }
        }
        _logger.LogInformation("ASR sidecar back online — transcription queue resumed");
    }

    /// <summary>执行单条转写并把结果写入磁盘与元数据。</summary>
    public async Task TranscribeAsync(long id, CancellationToken ct = default)
    {
        var rec = _db.GetById(id);
        if (rec is null)
        {
            _logger.LogWarning("Transcribe skipped: recording {Id} not found", id);
            return;
        }
        if (rec.NoTranscribe)
        {
            // 入队后被标记「默认不转写」：退回未转写状态，不再执行
            _logger.LogInformation("Transcribe skipped: id={Id} marked no-transcribe", id);
            _db.UpdateTranscript(id, "", "none", null);
            return;
        }
        if (!File.Exists(rec.StoredPath))
        {
            _logger.LogWarning("Transcribe failed: file missing {Path}", rec.StoredPath);
            _db.UpdateTranscript(id, "", "error", null);
            return;
        }

        // 实际耗时计时：开始时间供前端显示「已进行」，收尾落库总耗时
        _db.SetTranscribeStarted(id, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _db.UpdateTranscript(id, "", "pending", null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 边车每产出一段即回调：进度与部分文本实时落库，前端轮询即可流式展示
            var result = await _asr.TranscribeAsync(id, rec.StoredPath,
                onProgress: (progress, partial) => _db.UpdateTranscriptProgress(id, progress, partial),
                ct: ct);
            if (result is null)
            {
                _logger.LogWarning("ASR sidecar returned no result for id={Id}", id);
                // 边车临时不可用属可重试错误：计次，闲时窗口内自动重试
                _db.UpdateTranscript(id, "", "error", null, attemptsDelta: +1);
                return;
            }

            var tDir = Path.Combine(_cfg.DataDir, "transcripts");
            Directory.CreateDirectory(tDir);
            var tPath = Path.Combine(tDir, $"{id}.json");
            await File.WriteAllTextAsync(tPath, JsonSerializer.Serialize(result, _json), ct);

            // 全文与时长落库：全文供关键词检索（列 transcript_text），时长取末段 end（秒）
            var plainText = !string.IsNullOrWhiteSpace(result.Text)
                ? result.Text!
                : string.Join("\n", (result.Segments ?? []).Select(s => s.Text ?? ""));
            var durationSec = (result.Segments is { Count: > 0 }) ? result.Segments[^1].End : 0;
            if (plainText.Length > 0 || durationSec > 0)
                _db.SaveTranscriptResult(id, plainText, durationSec);

            _db.UpdateTranscript(id, tPath, "done", result.Language, attemptsDelta: -1_000_000); // 成功清零重试计数
            _logger.LogInformation("Transcribed id={Id} lang={Lang} segments={Seg} duration={Dur:F1}s elapsed={Elapsed:F1}s",
                id, result.Language, result.Segments?.Count ?? 0, durationSec, sw.Elapsed.TotalSeconds);

            AdaptSpeedFactor(durationSec, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            sw.Stop();
            _db.SetTranscribeElapsed(id, sw.ElapsedMilliseconds); // 成功与失败均记录本次耗时
        }
    }

    /// <summary>
    /// 按本次实际转写修正全局速度因子（实时倍率 = 音频时长 / 实际耗时）。
    /// EMA 平滑（0.7 旧值 + 0.3 本次）避免单条异常数据引起大幅波动；
    /// 持久化到 settings.json，重启后估算仍然准确。
    /// </summary>
    private void AdaptSpeedFactor(double audioSec, double elapsedSec)
    {
        if (audioSec <= 1 || elapsedSec <= 0.5) return; // 时长过短/异常快的样本不可信
        var actual = audioSec / elapsedSec;
        var next = _cfg.TranscribeSpeed <= 0 ? actual : _cfg.TranscribeSpeed * 0.7 + actual * 0.3;
        _cfg.TranscribeSpeed = Math.Clamp(next, 0.2, 100);
        try
        {
            _settings.Save(new StoredSettings
            {
                Asr = _cfg.Asr,
                Retention = _cfg.Retention,
                PageSize = _cfg.PageSize,
                TranscribeSpeed = _cfg.TranscribeSpeed
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist transcribe speed factor");
        }
    }
}
