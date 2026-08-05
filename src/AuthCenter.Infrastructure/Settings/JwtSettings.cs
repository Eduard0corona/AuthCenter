namespace AuthCenter.Infrastructure.Settings;

public class JwtSettings
{
    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string SigningKey { get; init; } = string.Empty;
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 30;
    public int MagicLinkTokenMinutes { get; init; } = 15;
    /// <summary>Active signing key. Every access and ID token is signed with this one.</summary>
    public string RsaPrivateKeyPem { get; init; } = string.Empty;

    /// <summary>
    /// Keys that are no longer used for signing but are still accepted on validation and published
    /// in the JWKS, so that a rotation does not invalidate tokens already in circulation. Public
    /// or private PEM are both accepted; only the public half is ever exposed.
    /// </summary>
    public string[] AdditionalValidationKeysPem { get; init; } = [];
}
