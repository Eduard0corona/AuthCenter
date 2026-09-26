using AuthCenter.Contracts.Requests.Common;

namespace AuthCenter.Contracts.Requests.Saml;

/// <summary>An attribute of the assertions: its SAML name and its source (email, name, userId, roles, permissions, groups or profile:key).</summary>
public sealed class SamlAttributeMappingRequest
{
    public string Name { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
}

public class SamlServiceProviderFields
{
    public string Name { get; init; } = string.Empty;
    public string EntityId { get; init; } = string.Empty;
    /// <summary>HTTPS URLs; the first is the default.</summary>
    public IReadOnlyList<string> AssertionConsumerServiceUrls { get; init; } = [];
    public string? SingleLogoutServiceUrl { get; init; }
    public string NameIdFormat { get; init; } = "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress";
    /// <summary>PEM or base64 DER.</summary>
    public string? SigningCertificate { get; init; }
    public bool RequireSignedRequests { get; init; }
    /// <summary>PEM or base64 DER.</summary>
    public string? EncryptionCertificate { get; init; }
    public bool EncryptAssertions { get; init; }
    public bool SignResponse { get; init; } = true;
    public IReadOnlyList<SamlAttributeMappingRequest> Attributes { get; init; } = [];
    public bool AllowIdpInitiated { get; init; }
    public string? DefaultRelayState { get; init; }
    public int AssertionLifetimeMinutes { get; init; } = 5;
}

public sealed class CreateSamlServiceProviderRequest : SamlServiceProviderFields
{
    public Guid ApplicationSystemId { get; init; }
}

public sealed class UpdateSamlServiceProviderRequest : SamlServiceProviderFields
{
    public bool IsActive { get; init; } = true;
    /// <summary>The version that was loaded; an older one is refused with 409.</summary>
    public long? Version { get; init; }
}

public sealed class SamlServiceProviderQuery : PaginationQuery
{
    public Guid? ApplicationSystemId { get; init; }
    public string? Search { get; init; }
    public bool? IsActive { get; init; }
}

public sealed class ParseSamlMetadataRequest
{
    public string MetadataXml { get; init; } = string.Empty;
}
