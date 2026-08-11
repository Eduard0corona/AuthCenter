using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

public sealed class FederationProvider
{
    public Guid Id { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public FederationProtocol Protocol { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string? DiscoveryEndpoint { get; set; }
    public string? ClientId { get; set; }
    public string? OidcCallbackUrl { get; set; }
    public string? ProtectedClientSecret { get; set; }
    public string? SamlSingleSignOnUrl { get; set; }
    public string? SamlSigningCertificatePem { get; set; }
    public bool JitProvisioningEnabled { get; set; }
    public AccountLinkingMode AccountLinkingMode { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public ApplicationSystem ApplicationSystem { get; set; } = null!;
    public ICollection<FederationRoutingRule> RoutingRules { get; set; } = new List<FederationRoutingRule>();
}
