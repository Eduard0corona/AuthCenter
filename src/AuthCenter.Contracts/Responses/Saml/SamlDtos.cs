namespace AuthCenter.Contracts.Responses.Saml;

public sealed class SamlCertificateDto
{
    public string Pem { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string ThumbprintSha256 { get; init; } = string.Empty;
    public DateTime NotBefore { get; init; }
    public DateTime NotAfter { get; init; }
}

public sealed class SamlAttributeMappingDto
{
    public string Name { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
}

public sealed class SamlServiceProviderDto
{
    /// <summary>Send it back when updating: an update of an older version is rejected with 409.</summary>
    public long Version { get; init; }
    public Guid Id { get; init; }
    public Guid ApplicationSystemId { get; init; }
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string EntityId { get; init; } = string.Empty;
    public IReadOnlyList<string> AssertionConsumerServiceUrls { get; init; } = [];
    public string? SingleLogoutServiceUrl { get; init; }
    public string NameIdFormat { get; init; } = string.Empty;
    public SamlCertificateDto? SigningCertificate { get; init; }
    public bool RequireSignedRequests { get; init; }
    public SamlCertificateDto? EncryptionCertificate { get; init; }
    public bool EncryptAssertions { get; init; }
    public bool SignResponse { get; init; }
    public IReadOnlyList<SamlAttributeMappingDto> Attributes { get; init; } = [];
    public bool AllowIdpInitiated { get; init; }
    public string? DefaultRelayState { get; init; }
    /// <summary>Where users start the sign-in from AuthCenter, when allowed.</summary>
    public string? LaunchUrl { get; init; }
    public int AssertionLifetimeMinutes { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

/// <summary>What a service provider's metadata says, to fill the registration form.</summary>
public sealed class SamlServiceProviderMetadataDto
{
    public string EntityId { get; init; } = string.Empty;
    public IReadOnlyList<string> AssertionConsumerServiceUrls { get; init; } = [];
    public string? SingleLogoutServiceUrl { get; init; }
    public string? NameIdFormat { get; init; }
    public string? SigningCertificate { get; init; }
    public string? EncryptionCertificate { get; init; }
    public bool RequireSignedRequests { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>AuthCenter as an identity provider: what to register in the service provider.</summary>
public sealed class SamlIdentityProviderDto
{
    public bool IsConfigured { get; init; }
    public string? Problem { get; init; }
    public string EntityId { get; init; } = string.Empty;
    public string MetadataUrl { get; init; } = string.Empty;
    public string SingleSignOnUrl { get; init; } = string.Empty;
    public string SingleLogoutUrl { get; init; } = string.Empty;
    public SamlCertificateDto? Certificate { get; init; }
    public IReadOnlyList<string> NameIdFormats { get; init; } = [];
    public IReadOnlyList<string> AttributeSources { get; init; } = [];
}
