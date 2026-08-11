namespace AuthCenter.Contracts.Requests.Policies;

public sealed class SimulateAccessPolicyRequest
{
    public Guid ApplicationSystemId { get; init; }
    public Guid UserId { get; init; }
    public Guid? PolicyVersionId { get; init; }
    public string? IpAddress { get; init; }
    public DateTime? EvaluatedAtUtc { get; init; }
    public string RiskLevel { get; init; } = "Unknown";
    public string AssuranceLevel { get; init; } = "Password";
}
