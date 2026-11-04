using System.Text.RegularExpressions;

namespace VoiceVault.Server.Services;

/// <summary>
/// 联系人/来源分类提取：从录音相对路径推断分类键。
///   微信类：路径含「微信 / WeChat / weixin」时提取联系人备注（如「微信录音 李宜峰_20261104162152.aac」→ 李宜峰）；
///   电话类：文件名含 11 位手机号（如 call_13812345678_...）→ 以号码为分类键；
///   其余 → 未分类（contact 为空，列表归入「全部来源」）。
/// 提取结果落库（contact / contact_kind 列），上传时计算一次，启动时对存量数据回填。
/// </summary>
public static class ContactExtractor
{
    private static readonly Regex PhoneRegex = new(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.Compiled);

    // 「微信录音 联系人_时间」或「微信_联系人_时间」等，取前缀词之后的联系人段
    private static readonly Regex WechatNameRegex =
        new(@"微信(?:录音|通话)?[\s_]*([^\-_]+?)[\s_]*\d{6,}", RegexOptions.Compiled);

    private static readonly string[] WechatMarkers = ["微信", "WeChat", "wechat", "weixin", "Weixin"];

    public static (string? Contact, string? Kind) Extract(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return (null, null);
        var fileName = Path.GetFileNameWithoutExtension(relPath);
        var dirName = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? "";

        // 1) 微信类：目录或文件名带微信标记 → 提取联系人备注
        if (WechatMarkers.Any(dirName.Contains) || WechatMarkers.Any(fileName.Contains))
        {
            var name = WechatNameRegex.Match(fileName).Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(name))
            {
                // 兜底：去掉「微信录音/通话」等前缀词后取第一段
                var stem = fileName
                    .Replace("微信录音", "").Replace("微信通话", "").Replace("微信", "")
                    .Trim('_');
                name = stem.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(s => s.Length is >= 1 and <= 32 && !IsPureNumber(s)) ?? stem;
            }
            name = name.Trim('_').Trim();
            if (name.Length is >= 1 and <= 32)
                return (name, "wechat");
        }

        // 2) 电话类：文件名含手机号（小米/华为通话录音常见 call_号码_时间 或 号码_时间）
        var phone = PhoneRegex.Match(fileName).Value;
        if (!string.IsNullOrEmpty(phone))
            return (phone, "phone");

        return (null, null);
    }

    private static bool IsPureNumber(string s) => s.All(char.IsDigit);
}
