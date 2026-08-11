namespace AuthCenter.Infrastructure.Settings;

public sealed class AdaptiveAuthenticationSettings
{
    public string SignalHashKey { get; init; } = string.Empty;
    public int ObservationRetentionDays { get; init; } = 30;
    public int FailedEventWindowMinutes { get; init; } = 15;
    public int FailedEventThreshold { get; init; } = 5;
    public int MaximumTravelSpeedKmh { get; init; } = 900;
}
