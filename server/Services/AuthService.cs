using System.Security.Cryptography;
using System.Text;

namespace VoiceVault.Server.Services;

/// <summary>
/// 简单的 Bearer Token 鉴权。生产环境建议替换为更强方案（如每设备令牌）。
/// 令牌可运行期修改（Web 设置页"修改服务端令牌"），修改后持久化到
/// settings.json 并立即生效，无需重启容器。
/// </summary>
public class AuthService
{
    private readonly object _lock = new();
    private string _token;
    private byte[] _tokenBytes;

    public AuthService(string token)
    {
        _token = token;
        _tokenBytes = Encoding.UTF8.GetBytes(token);
    }

    /// <summary>当前生效令牌（用于日志提示等只读场景）。</summary>
    public string CurrentToken { get { lock (_lock) return _token; } }

    /// <summary>运行期更换令牌：原子替换，新请求立即用新值校验。</summary>
    public void Update(string newToken)
    {
        lock (_lock)
        {
            _token = newToken;
            _tokenBytes = Encoding.UTF8.GetBytes(newToken);
        }
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
        lock (_lock)
        {
            return CryptographicOperations.FixedTimeEquals(candidate, _tokenBytes);
        }
    }
}
