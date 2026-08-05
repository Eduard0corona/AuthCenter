namespace AuthCenter.Infrastructure.Settings;

public class JwtSettings
{
    public const string RsaKeyId = "authcenter-key-1";

    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string SigningKey { get; init; } = string.Empty;
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 30;
    public int MagicLinkTokenMinutes { get; init; } = 15;
    public string RsaPrivateKeyPem { get; init; } = string.Empty;
}
