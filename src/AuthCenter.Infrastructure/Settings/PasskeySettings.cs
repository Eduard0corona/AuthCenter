namespace AuthCenter.Infrastructure.Settings;

public sealed class PasskeySettings
{
    public string RelyingPartyId { get; init; } = string.Empty;
    public string[] AllowedOrigins { get; init; } = [];
    public int CeremonyMinutes { get; init; } = 5;
    public int ReauthenticationMinutes { get; init; } = 5;
    public int MaxCredentialsPerUser { get; init; } = 10;
}
