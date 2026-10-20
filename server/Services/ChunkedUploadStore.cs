using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceVault.Server.Services;

/// <summary>
/// 分片上传会话：落盘临时分片，支持断点续传。
/// uploadId 由 (device|relPath|size|mtime) 派生——同一文件中断后重新 init
/// 必命中同一会话目录，已传分片直接复用，无需客户端持久化任何状态。
/// 会话目录：<c>{DataDir}/tmp/chunk/{uploadId}/{index}.part</c> + meta.json。
/// </summary>
public class ChunkedUploadStore
{
    private record ChunkMeta(
        string RelPath, string Device, long Mtime, long Size,
        int ChunkSize, int TotalChunks, long CreatedAt);

    private readonly string _root;
    private readonly ILogger<ChunkedUploadStore> _logger;

    /// <summary>僵尸会话保留期：超过后可被清理（容器重启不丢分片）。</summary>
    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(24);

    public ChunkedUploadStore(string dataDir, ILogger<ChunkedUploadStore> logger)
    {
        _root = Path.Combine(dataDir, "tmp", "chunk");
        Directory.CreateDirectory(_root);
        _logger = logger;
    }

    private static string SanitizeId(string id)
    {
        // uploadId 只允许十六进制字符（ComputeId 输出即纯 hex），杜绝路径穿越
        return !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(char.IsAsciiHexDigit)
            ? id : "";
    }

    private static string ComputeId(string device, string relPath, long size, long mtime)
    {
        var raw = $"{device}|{relPath}|{size}|{mtime}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private string SessionDir(string uploadId) => Path.Combine(_root, uploadId);
    private string MetaPath(string uploadId) => Path.Combine(SessionDir(uploadId), "meta.json");
    private string PartPath(string uploadId, long index) =>
        Path.Combine(SessionDir(uploadId), $"{index}.part");

    /// <summary>
    /// 初始化（或续传探测）会话。若会话已存在且元数据一致，返回已收分片列表；
    /// 元数据不一致（极端重名冲突）时清空重建。顺带清理过期会话。
    /// </summary>
    public (string UploadId, List<long> Received) Init(
        string device, string relPath, long mtime, long size, int chunkSize, int totalChunks)
    {
        CleanupExpired();

        var id = ComputeId(device, relPath, size, mtime);
        var dir = SessionDir(id);
        var meta = new ChunkMeta(relPath, device, mtime, size, chunkSize, totalChunks,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        if (Directory.Exists(dir))
        {
            var existing = TryReadMeta(id);
            if (existing is null || existing.Size != size || existing.TotalChunks != totalChunks
                || existing.RelPath != relPath || existing.Device != device || existing.Mtime != mtime)
            {
                // 参数不一致：整目录重建，避免新旧分片混拼
                Directory.Delete(dir, recursive: true);
                _logger.LogWarning("Chunk session {Id} metadata mismatch, rebuilt", id);
            }
        }

        Directory.CreateDirectory(dir);
        if (!File.Exists(MetaPath(id)))
            WriteMetaSafe(id, meta);

        var received = new List<long>();
        for (var i = 0; i < totalChunks; i++)
            if (File.Exists(PartPath(id, i))) received.Add(i);
        return (id, received);
    }

    /// <summary>写入一个分片（临时文件 + rename 原子落盘，重传幂等）。失败返回错误消息，成功返回 null。</summary>
    public async Task<string?> WriteChunkAsync(string uploadId, long index, Stream body, CancellationToken ct = default)
    {
        var id = SanitizeId(uploadId);
        if (id.Length == 0) return "invalid uploadId";

        var meta = TryReadMeta(id);
        if (meta is null) return "session not found (init first)";
        if (index < 0 || index >= meta.TotalChunks)
            return $"index out of range (0..{meta.TotalChunks - 1})";

        var dir = SessionDir(id);
        var tmp = Path.Combine(dir, $"{index}.part.tmp");
        var target = PartPath(id, index);

        try
        {
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await body.CopyToAsync(fs, ct);
            await fs.FlushAsync(ct);
            fs.Close();
            File.Move(tmp, target, overwrite: true); // 原子替换，重传幂等
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 尽力清理 */ }
            return "failed to write chunk: " + ex.Message;
        }
    }

    /// <summary>
    /// 会话信息：元数据与各分片路径（按序）。分片不齐时返回 missing 列表。
    /// </summary>
    public (ChunkSession? Session, List<long> Missing) GetComplete(string uploadId)
    {
        var id = SanitizeId(uploadId);
        var meta = TryReadMeta(id);
        if (meta is null) return (null, new List<long> { -1 });

        var missing = new List<long>();
        var paths = new List<string>();
        for (var i = 0; i < meta.TotalChunks; i++)
        {
            var p = PartPath(id, i);
            if (File.Exists(p)) paths.Add(p);
            else missing.Add(i);
        }
        return (new ChunkSession(id, meta.RelPath, meta.Device, meta.Mtime, meta.Size, paths), missing);
    }

    /// <summary>删除会话目录（complete 成功或显式取消时调用）。</summary>
    public void Remove(string uploadId)
    {
        var id = SanitizeId(uploadId);
        if (id.Length == 0) return;
        try { if (Directory.Exists(SessionDir(id))) Directory.Delete(SessionDir(id), recursive: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove chunk session {Id}", id); }
    }

    /// <summary>清理超过 TTL 的僵尸会话（init 时触发，低成本兜底）。</summary>
    private void CleanupExpired()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                var metaPath = Path.Combine(dir, "meta.json");
                DateTime created;
                try { created = File.GetLastWriteTimeUtc(metaPath); }
                catch { continue; }
                if (DateTime.UtcNow - created > SessionTtl)
                {
                    Directory.Delete(dir, recursive: true);
                    _logger.LogInformation("Expired chunk session removed: {Dir}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chunk session cleanup failed");
        }
    }

    private ChunkMeta? TryReadMeta(string id)
    {
        try
        {
            var path = MetaPath(id);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ChunkMeta>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private void WriteMetaSafe(string id, ChunkMeta meta)
    {
        var tmp = MetaPath(id) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(meta));
        File.Move(tmp, MetaPath(id), overwrite: true);
    }
}

/// <summary>一个分片上传会话的只读快照。</summary>
public record ChunkSession(
    string UploadId, string RelPath, string Device, long Mtime, long Size,
    IReadOnlyList<string> PartPaths);

/// <summary>
/// 顺序拼接多个分片文件的只读流（零拷贝：不落中间完整文件），
/// 交给 StorageService.SaveAsync 流式计算 SHA 与落盘。
/// 仅支持顺序读（SaveAsync 的用法），不支持 Seek。
/// </summary>
internal sealed class ConcatenatedStream : Stream
{
    private readonly IReadOnlyList<string> _paths;
    private FileStream? _current;
    private int _index = -1;
    private bool _ended;

    public ConcatenatedStream(IReadOnlyList<string> paths) => _paths = paths;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!_ended)
        {
            if (_current is null)
            {
                _index++;
                if (_index >= _paths.Count) { _ended = true; break; }
                _current = new FileStream(_paths[_index], FileMode.Open, FileAccess.Read,
                    FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }

            var n = await _current.ReadAsync(buffer, cancellationToken);
            if (n > 0) return n;

            // 当前分片读完，切下一片
            await _current.DisposeAsync();
            _current = null;
        }
        return 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _current?.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_current is not null) await _current.DisposeAsync();
        _current = null;
        await base.DisposeAsync();
    }
}
