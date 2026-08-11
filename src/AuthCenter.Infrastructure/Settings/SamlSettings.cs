namespace AuthCenter.Infrastructure.Settings;

public sealed class SamlSettings
{
    public string EntityId { get; init; } = string.Empty;
    public string AssertionConsumerServiceUrl { get; init; } = string.Empty;
    public string SigningCertificateBase64 { get; init; } = string.Empty;
    public string? SigningCertificatePassword { get; init; }
    public int ClockSkewSeconds { get; init; } = 120;
}
