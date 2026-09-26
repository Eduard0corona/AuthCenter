using AuthCenter.Domain.Common;

namespace AuthCenter.Domain.Entities;

/// <summary>
/// An application that signs users in with SAML 2.0, AuthCenter acting as its identity provider.
/// Access follows the owning application: its users, access policy and MFA requirement apply to the
/// assertions issued for it.
/// </summary>
public class SamlServiceProvider : IVersionedEntity
{
    public long Version { get; set; }

    public Guid Id { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The service provider's SAML entity ID: the issuer of its requests and the assertions' audience.</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>The HTTPS URLs assertions may be posted to; the first one is the default.</summary>
    public string AssertionConsumerServiceUrlsJson { get; set; } = "[]";

    /// <summary>Where logout responses are sent after a logout the service provider started.</summary>
    public string? SingleLogoutServiceUrl { get; set; }

    public string NameIdFormat { get; set; } = SamlNameIdFormats.EmailAddress;

    /// <summary>Random per provider: persistent name identifiers cannot be correlated across providers.</summary>
    public string NameIdSalt { get; set; } = string.Empty;

    /// <summary>The service provider's certificate (base64 DER) that verifies its signed requests.</summary>
    public string? SigningCertificate { get; set; }
    public bool RequireSignedRequests { get; set; }

    /// <summary>The service provider's certificate (base64 DER) assertions are encrypted for.</summary>
    public string? EncryptionCertificate { get; set; }
    public bool EncryptAssertions { get; set; }

    /// <summary>The assertion is always signed; this also signs the enclosing response.</summary>
    public bool SignResponse { get; set; } = true;

    /// <summary>Attribute statements: [{ "name": "...", "source": "email|name|userId|roles|groups|profile:key" }].</summary>
    public string AttributesJson { get; set; } = "[]";

    /// <summary>Whether users may start the sign-in from AuthCenter (the portal), without a request.</summary>
    public bool AllowIdpInitiated { get; set; }
    public string? DefaultRelayState { get; set; }
    public int AssertionLifetimeMinutes { get; set; } = 5;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ApplicationSystem ApplicationSystem { get; set; } = null!;
}

public static class SamlNameIdFormats
{
    public const string EmailAddress = "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress";
    public const string Persistent = "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent";
    public const string Unspecified = "urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified";
    public const string Transient = "urn:oasis:names:tc:SAML:2.0:nameid-format:transient";

    public static readonly IReadOnlyList<string> Supported = [EmailAddress, Persistent, Unspecified];
}
