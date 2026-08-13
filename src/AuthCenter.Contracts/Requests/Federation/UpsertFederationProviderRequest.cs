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
    public bool IsActive { get; init; } = true;
    public long Version { get; init; }
}
