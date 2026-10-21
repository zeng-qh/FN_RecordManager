using System.Text.Json;
using System.Threading.Channels;
using VoiceVault.Server.Models;

namespace VoiceVault.Server.Services;

/// <summary>
/// 后台转写队列：所有转写任务（手动触发 / 上传后自动触发）统一排队，
/// 逐条串行执行，避免并发打满 NAS 资源。
/// </summary>
public class TranscriptionQueue : BackgroundService
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _cts = new();
    private readonly DbService _db;
    private readonly AsrClient _asr;
    private readonly VaultConfig _cfg;
    private readonly ILogger<TranscriptionQueue> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public TranscriptionQueue(
        DbService db, AsrClient asr, VaultConfig cfg, ILogger<TranscriptionQueue> logger)
    {
        _db = db;
        _asr = asr;
        _cfg = cfg;
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

    /// <summary>执行单条转写并把结果写入磁盘与元数据。</summary>
    public async Task TranscribeAsync(long id, CancellationToken ct = default)
    {
        var rec = _db.GetById(id);
        if (rec is null)
        {
            _logger.LogWarning("Transcribe skipped: recording {Id} not found", id);
            return;
        }
        if (!File.Exists(rec.StoredPath))
        {
            _logger.LogWarning("Transcribe failed: file missing {Path}", rec.StoredPath);
            _db.UpdateTranscript(id, "", "error", null);
            return;
        }

        _db.UpdateTranscript(id, "", "pending", null);
        var result = await _asr.TranscribeAsync(rec.StoredPath, ct);
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
        _logger.LogInformation("Transcribed id={Id} lang={Lang} segments={Seg} duration={Dur:F1}s",
            id, result.Language, result.Segments?.Count ?? 0, durationSec);
    }
}
