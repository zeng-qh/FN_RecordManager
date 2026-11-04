namespace VoiceVault.Server.Models;

using System.Text.Json.Serialization;

/// <summary>
/// 一条已备份录音在服务端的元数据。
/// </summary>
public class Recording
{
    public long Id { get; set; }

    /// <summary>来源设备标识（如手机序列号/自定义名）。</summary>
    public string Device { get; set; } = "";

    /// <summary>手机上的原始相对路径，如 MIUI/sound_recorder/xxx.m4a。</summary>
    public string RelPath { get; set; } = "";

    /// <summary>文件名（用于展示与检索）。</summary>
    public string FileName { get; set; } = "";

    /// <summary>
    /// 服务端落盘的绝对路径。仅服务端内部使用（删除/播放寻址），
    /// 序列化给前端时隐藏，避免泄露服务器文件系统结构。
    /// </summary>
    [JsonIgnore]
    public string StoredPath { get; set; } = "";

    /// <summary>字节大小。</summary>
    public long Size { get; set; }

    /// <summary>手机端文件修改时间（Unix 秒）。</summary>
    public long Mtime { get; set; }

    /// <summary>文件 SHA-256（十六进制小写），用于增量去重。</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>可读标题（默认取自文件名，可后续编辑）。</summary>
    public string? Title { get; set; }

    /// <summary>录音时间（Unix 秒），由 mtime 推导，用于时间线分组。</summary>
    public long RecordedAt { get; set; }

    /// <summary>转写状态：none / idle（待闲时）/ pending / done / error。</summary>
    public string? TranscriptStatus { get; set; } = "none";

    /// <summary>
    /// 转写结果 JSON 的路径。仅服务端内部使用（读取/删除寻址），
    /// 序列化给前端时隐藏（前端经 /transcript 端点取内容）。
    /// </summary>
    [JsonIgnore]
    public string? TranscriptPath { get; set; }

    /// <summary>识别出的语言代码。</summary>
    public string? Language { get; set; }

    /// <summary>录音时长（秒）；转写完成后由末段 end 提取，未转写时为空。</summary>
    public double? DurationSec { get; set; }

    /// <summary>转写已失败次数（闲时窗口内自动重试，达上限后仅可手动重试）。</summary>
    public int TranscriptAttempts { get; set; }

    /// <summary>转写进度（0-100）；进行中实时更新，完成/失败后不再变化。</summary>
    public double TranscriptProgress { get; set; }

    /// <summary>转写进行中已产出的部分文本（流式预览），完成后清空。</summary>
    public string? TranscriptPartial { get; set; }

    /// <summary>用户备注（参与关键词搜索）。</summary>
    public string? Note { get; set; }

    /// <summary>联系人/来源分类键：电话号码或微信联系人备注（上传时从文件名自动提取，可手动改）。</summary>
    public string? Contact { get; set; }

    /// <summary>分类类型：phone（电话号码）/ wechat（微信联系人）。</summary>
    public string? ContactKind { get; set; }

    /// <summary>入库时间（ISO8601）。</summary>
    public DateTime CreatedAt { get; set; }
}
