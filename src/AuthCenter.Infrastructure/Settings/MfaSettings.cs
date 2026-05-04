namespace AuthCenter.Infrastructure.Settings;

public class MfaSettings
{
    public string EncryptionKey { get; init; } = string.Empty;
    public string TotpIssuer { get; init; } = "AuthCenter";
    public int MfaTokenExpirySeconds { get; init; } = 300;
}
