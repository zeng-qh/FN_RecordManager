namespace VoiceVault.Server.Models;

/// <summary>批量转写入队请求：all=true 一键转写全部未转写；否则按 ids 入队（跳过「不转写」标记的）。</summary>
public record TranscribeEnqueueRequest(List<long>? Ids, bool All = false);

/// <summary>批量设置「默认不转写」标记请求。</summary>
public record TranscribeSkipRequest(List<long>? Ids, bool Value);

/// <summary>单条录音时长回填项（前端经 audio 元数据读出后写回）。</summary>
public record DurationPatch(long Id, double DurationSec);
