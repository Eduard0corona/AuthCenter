namespace AuthCenter.Infrastructure.Settings;

public sealed class RetentionSettings
{
    public bool Enabled { get; init; } = true;
    public int IntervalHours { get; init; } = 6;
    public int TokenHistoryDays { get; init; } = 30;
    public int AuditLogDays { get; init; } = 365;
    public int BatchSize { get; init; } = 500;
}
