namespace AuthCenter.Infrastructure.Settings;

public sealed class SamlSettings
{
    public string EntityId { get; init; } = string.Empty;
    public string AssertionConsumerServiceUrl { get; init; } = string.Empty;
    public string SigningCertificateBase64 { get; init; } = string.Empty;
    public string? SigningCertificatePassword { get; init; }
    public int ClockSkewSeconds { get; init; } = 120;

    /// <summary>
    /// The entity ID of AuthCenter as an identity provider; by default its metadata URL. The same
    /// certificate signs its assertions.
    /// </summary>
    public string? IdentityProviderEntityId { get; init; }
}
