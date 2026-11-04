using System.Security.Cryptography;
using System.Text;

namespace VoiceVault.Server.Services;

/// <summary>
/// 简单的 Bearer Token 鉴权。生产环境建议替换为更强方案（如每设备令牌）。
/// </summary>
public class AuthService
{
    private readonly string _token;
    private readonly byte[] _tokenBytes;

    public AuthService(string token)
    {
        _token = token;
        _tokenBytes = Encoding.UTF8.GetBytes(token);
    }

    public bool Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();

        if (string.IsNullOrWhiteSpace(token))
            return false;

        // 恒定时间比较：无论 token 是否命中、在第几位不同，耗时一致，
        // 避免逐字节提前短路造成的时序侧信道。
        var candidate = Encoding.UTF8.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(candidate, _tokenBytes);
    }
}
