namespace AuthCenter.Contracts.Responses.Federation;

public sealed class FederationProviderDto
{
    public Guid Id { get; init; }
    public Guid ApplicationSystemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string Issuer { get; init; } = string.Empty;
    public string? DiscoveryEndpoint { get; init; }
    public string? ClientId { get; init; }
    public string? OidcCallbackUrl { get; init; }
    public bool HasClientSecret { get; init; }
    public string? SamlSingleSignOnUrl { get; init; }
    public string? SamlSigningCertificateThumbprint { get; init; }
    public bool JitProvisioningEnabled { get; init; }
    public string AccountLinkingMode { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}
