namespace AuthCenter.Contracts.Responses;

public sealed class AdminDashboardDto
{
    public DateTime GeneratedAt { get; init; }
    public int ActiveUsers { get; init; }
    public int InactiveUsers { get; init; }
    public int ActiveApplications { get; init; }
    public int ActiveGroups { get; init; }
    /// <summary>Application access requests waiting for an administrator's approval.</summary>
    public int PendingAccessRequests { get; init; }
    public int ActiveFederationProviders { get; init; }
    public int ExpiringProvisioningTokens { get; init; }
    public int UnverifiedEventHooks { get; init; }
    public int DeadLetterDeliveries { get; init; }
    /// <summary>Rejected sign-ins in the last 24 hours: passwords, lockouts, MFA codes, passkeys and federation.</summary>
    public int FailedLoginsLast24Hours { get; init; }
    public int HighRiskObservationsLast24Hours { get; init; }
}
