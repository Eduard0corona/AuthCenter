namespace AuthCenter.Application.Models;

/// <summary>How a SAML sign-in reached the identity provider.</summary>
public enum SamlRequestBinding { Redirect, Post, IdpInitiated }

/// <summary>
/// A SAML sign-in to start: an AuthnRequest of the HTTP-Redirect or HTTP-POST binding, or a
/// sign-in the user starts from AuthCenter for a service provider (no request).
/// </summary>
public sealed record SamlSignInStart(SamlRequestBinding Binding, string? SamlRequest, string? RelayState, string? RawQuery, Guid? ServiceProviderId = null);

/// <summary>A form the browser posts to a service provider: SAMLResponse and RelayState.</summary>
public sealed record SamlPostMessage(string Destination, IReadOnlyList<KeyValuePair<string, string>> Fields);

/// <summary>
/// What the browser gets after a SAML request: a message to post to the service provider, a
/// redirect to the hosted login, or an error page when the request cannot be trusted (it is never
/// answered at an address the provider did not register).
/// </summary>
public sealed record SamlEndpointOutcome(SamlPostMessage? Post, string? RedirectUrl, int ErrorStatus, string? ErrorMessage, bool EndedBrowserSession = false)
{
    public static SamlEndpointOutcome Posted(SamlPostMessage message, bool endedBrowserSession = false) => new(message, null, 0, null, endedBrowserSession);
    public static SamlEndpointOutcome Redirect(string url) => new(null, url, 0, null);
    public static SamlEndpointOutcome Error(int status, string message) => new(null, null, status, message);
}

/// <summary>The identity provider's own values, for its metadata and for administrators.</summary>
public sealed record SamlIdentityProviderInfo(
    bool IsConfigured,
    string EntityId,
    string MetadataUrl,
    string SingleSignOnUrl,
    string SingleLogoutUrl,
    string? CertificatePem,
    string? CertificateThumbprintSha256,
    DateTime? CertificateNotBefore,
    DateTime? CertificateNotAfter,
    string? Problem);
