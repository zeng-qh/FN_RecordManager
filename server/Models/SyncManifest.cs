namespace VoiceVault.Server.Models;

/// <summary>
/// 客户端上报的单个文件清单项。
/// </summary>
public class FileEntry
{
    public string RelPath { get; set; } = "";
    public long Size { get; set; }
    public long Mtime { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// 增量同步请求：客户端把本地录音清单发给服务端求差。
/// </summary>
public class SyncDiffRequest
{
    public string Device { get; set; } = "";
    public List<FileEntry> Files { get; set; } = new();
}

/// <summary>
/// 增量同步响应：待上传清单 + 服务端已存在清单。
/// </summary>
public class SyncDiffResponse
{
    public List<string> ToUpload { get; set; } = new();
    public List<string> ServerHas { get; set; } = new();
}
