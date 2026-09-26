using AuthCenter.Domain.Common;
using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

public sealed class FederationProvider : IVersionedEntity
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

    /// <summary>
    /// OIDC only: the upstream must assert <c>email_verified=true</c>. When false the provider is
    /// trusted for the emails of its routing-rule domains and no other email is accepted.
    /// </summary>
    public bool RequireVerifiedEmail { get; set; } = true;

    /// <summary>An MFA reported by the upstream (OIDC <c>amr</c>, SAML authentication context) satisfies AuthCenter's MFA.</summary>
    public bool TrustUpstreamMfa { get; set; }

    /// <summary>Upstream claim (OIDC) or attribute (SAML) whose values drive <see cref="GroupMappings"/>.</summary>
    public string? GroupsClaim { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
    public ApplicationSystem ApplicationSystem { get; set; } = null!;
    public ICollection<FederationRoutingRule> RoutingRules { get; set; } = new List<FederationRoutingRule>();
    public ICollection<FederationGroupMapping> GroupMappings { get; set; } = new List<FederationGroupMapping>();
}
