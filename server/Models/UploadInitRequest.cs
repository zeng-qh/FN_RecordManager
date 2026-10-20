namespace VoiceVault.Server.Models;

/// <summary>
/// 分片上传初始化请求（网页端断点续传）。
/// uploadId 由服务端按 (device|relPath|size|mtime) 派生，客户端无需持久化。
/// </summary>
public class UploadInitRequest
{
    /// <summary>原始相对路径/文件名。</summary>
    public string RelPath { get; set; } = "";

    /// <summary>设备标识（网页端为 "web"）。</summary>
    public string Device { get; set; } = "";

    /// <summary>文件修改时间（Unix 秒）。</summary>
    public long Mtime { get; set; }

    /// <summary>文件总字节数。</summary>
    public long Size { get; set; }

    /// <summary>分片大小（字节），前端约定 4MB。</summary>
    public int ChunkSize { get; set; }

    /// <summary>分片总数。</summary>
    public int TotalChunks { get; set; }
}
