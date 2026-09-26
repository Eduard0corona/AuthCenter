namespace AuthCenter.Contracts.Responses;

public sealed class AdminDashboardDto
{
    public DateTime GeneratedAt { get; init; }
    public int ActiveUsers { get; init; }
    public int InactiveUsers { get; init; }
    public int ActiveApplications { get; init; }
    public int ActiveGroups { get; init; }
    /// <summary>Access requests waiting for a decision (portal requests, registrations and pending access).</summary>
    public int PendingAccessRequests { get; init; }
    /// <summary>Access reviews in course, and those of them past their due date.</summary>
    public int ActiveAccessReviews { get; init; }
    public int OverdueAccessReviews { get; init; }
    /// <summary>Accesses still to review in the active reviews.</summary>
    public int PendingAccessReviewItems { get; init; }
    /// <summary>Users holding both roles of an active separation of duties rule.</summary>
    public int SeparationOfDutiesViolations { get; init; }
    public int ActiveFederationProviders { get; init; }
    public int ExpiringProvisioningTokens { get; init; }
    public int UnverifiedEventHooks { get; init; }
    public int DeadLetterDeliveries { get; init; }
    /// <summary>Rejected sign-ins in the last 24 hours: passwords, lockouts, MFA codes, passkeys and federation.</summary>
    public int FailedLoginsLast24Hours { get; init; }
    public int HighRiskObservationsLast24Hours { get; init; }
}
