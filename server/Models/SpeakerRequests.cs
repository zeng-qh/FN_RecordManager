namespace VoiceVault.Server.Models;

/// <summary>声纹注册请求：从某条录音的某个说话人标注提取声纹，挂到 name 名下。</summary>
public class RegisterSpeakerRequest
{
    /// <summary>说话人名字（已存在则追加样本加固声纹）。</summary>
    public string Name { get; set; } = "";

    /// <summary>来源录音 id。</summary>
    public long RecordId { get; set; }

    /// <summary>来源说话人标签（转写里的「说话人N」）。</summary>
    public string SpeakerLabel { get; set; } = "";
}

/// <summary>声纹档案改名请求。</summary>
public class RenameSpeakerRequest
{
    public string Name { get; set; } = "";
}
