namespace AuthCenter.Contracts.Requests.Federation;

public sealed class UpsertFederationProviderRequest
{
    public Guid ApplicationSystemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string Issuer { get; init; } = string.Empty;
    public string? DiscoveryEndpoint { get; init; }
    public string? ClientId { get; init; }
    public string? OidcCallbackUrl { get; init; }
    public string? ClientSecret { get; init; }
    public string? SamlSingleSignOnUrl { get; init; }
    public string? SamlSigningCertificatePem { get; init; }
    public bool JitProvisioningEnabled { get; init; }
    public string AccountLinkingMode { get; init; } = "Disabled";

    /// <summary>OIDC only: require <c>email_verified=true</c> (default). See <c>FederationProvider.RequireVerifiedEmail</c>.</summary>
    public bool RequireVerifiedEmail { get; init; } = true;
    public bool TrustUpstreamMfa { get; init; }
    public string? GroupsClaim { get; init; }
    public IReadOnlyList<FederationGroupMappingItem> GroupMappings { get; init; } = [];
    public bool IsActive { get; init; } = true;
    public long Version { get; init; }
}

public sealed class FederationGroupMappingItem
{
    public string UpstreamValue { get; init; } = string.Empty;
    public Guid DirectoryGroupId { get; init; }
}
