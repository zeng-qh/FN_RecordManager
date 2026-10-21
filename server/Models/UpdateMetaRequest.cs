namespace VoiceVault.Server.Models;

/// <summary>
/// 更新录音标题/备注的请求。null 表示不修改，空字符串表示清空。
/// </summary>
public class UpdateMetaRequest
{
    public string? Title { get; set; }
    public string? Note { get; set; }
}
