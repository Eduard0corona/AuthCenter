namespace AuthCenter.Infrastructure.Settings;

/// <summary>Access governance: request lifetime and the background work that expires and completes items.</summary>
public sealed class GovernanceSettings
{
    /// <summary>Days a portal access request waits for a decision before it expires (1 to 365).</summary>
    public int AccessRequestLifetimeDays { get; init; } = 30;

    /// <summary>Pending portal requests a user may have at once (1 to 50).</summary>
    public int MaxPendingRequestsPerUser { get; init; } = 10;

    /// <summary>Runs the governance maintenance (expiring requests, completing and repeating reviews).</summary>
    public bool MaintenanceEnabled { get; init; } = true;

    /// <summary>Minutes between maintenance runs (1 to 1440).</summary>
    public int MaintenanceIntervalMinutes { get; init; } = 15;

    public int RequestLifetimeDays => Math.Clamp(AccessRequestLifetimeDays, 1, 365);
    public int PendingRequestLimit => Math.Clamp(MaxPendingRequestsPerUser, 1, 50);
    public TimeSpan MaintenanceInterval => TimeSpan.FromMinutes(Math.Clamp(MaintenanceIntervalMinutes, 1, 1440));
}
