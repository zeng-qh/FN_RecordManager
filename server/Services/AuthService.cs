namespace VoiceVault.Server.Services;

/// <summary>
/// 简单的 Bearer Token 鉴权。生产环境建议替换为更强方案（如每设备令牌）。
/// </summary>
public class AuthService
{
    private readonly string _token;

    public AuthService(string token)
    {
        _token = token;
    }

    public bool Validate(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return false;

        var header = authorizationHeader!;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            header = header["Bearer ".Length..].Trim();

        return !string.IsNullOrWhiteSpace(header) && header == _token;
    }
}
