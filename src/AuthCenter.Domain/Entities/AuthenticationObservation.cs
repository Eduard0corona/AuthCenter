using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

public sealed class AuthenticationObservation
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string NetworkHash { get; set; } = string.Empty;
    public string DeviceHash { get; set; } = string.Empty;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public AccessRiskLevel RiskLevel { get; set; }
    public string ReasonCodesJson { get; set; } = "[]";
    public DateTime ObservedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
}
