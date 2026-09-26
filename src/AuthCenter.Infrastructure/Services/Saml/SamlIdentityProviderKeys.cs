using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AuthCenter.Application.Interfaces;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services.Saml;

/// <summary>
/// The addresses of AuthCenter as a SAML identity provider (from its public issuer) and the
/// certificate that signs its assertions: the configured SAML certificate (<c>Saml:SigningCertificateBase64</c>).
/// </summary>
public sealed class SamlIdentityProviderKeys
{
    private readonly SamlSettings _saml;
    private readonly JwtSettings _jwt;
    private readonly IDateTimeProvider _clock;
    private readonly Lazy<(X509Certificate2? Certificate, string? Problem)> _certificate;

    public SamlIdentityProviderKeys(IOptions<SamlSettings> saml, IOptions<JwtSettings> jwt, IDateTimeProvider clock)
    {
        _saml = saml.Value;
        _jwt = jwt.Value;
        _clock = clock;
        _certificate = new Lazy<(X509Certificate2?, string?)>(Load);
    }

    private string Origin => _jwt.Issuer.TrimEnd('/');
    public string MetadataUrl => $"{Origin}/saml/idp/metadata";
    public string SingleSignOnUrl => $"{Origin}/saml/idp/sso";
    public string SingleLogoutUrl => $"{Origin}/saml/idp/slo";
    public string EntityId => string.IsNullOrWhiteSpace(_saml.IdentityProviderEntityId) ? MetadataUrl : _saml.IdentityProviderEntityId.Trim();
    public TimeSpan ClockSkew => TimeSpan.FromSeconds(Math.Clamp(_saml.ClockSkewSeconds, 0, 600));

    /// <summary>The certificate when it can sign now; otherwise null and why.</summary>
    public X509Certificate2? SigningCertificate(out string? problem)
    {
        var (certificate, loadProblem) = _certificate.Value;
        problem = loadProblem;
        if (certificate is null)
            return null;
        var now = _clock.UtcNow;
        if (certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now)
        {
            problem = "The SAML certificate is outside its validity period.";
            return null;
        }
        return certificate;
    }

    /// <summary>The configured certificate, even outside its validity (for administrators to see it).</summary>
    public X509Certificate2? ConfiguredCertificate => _certificate.Value.Certificate;

    private (X509Certificate2?, string?) Load()
    {
        if (string.IsNullOrWhiteSpace(_saml.SigningCertificateBase64))
            return (null, "Configure Saml:SigningCertificateBase64 with a PKCS#12 certificate and its RSA private key.");
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(_saml.SigningCertificateBase64), _saml.SigningCertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            using var key = certificate.GetRSAPrivateKey();
            return key is null ? (null, "The SAML certificate needs an RSA private key.") : (certificate, null);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return (null, "The SAML certificate cannot be read (Saml:SigningCertificateBase64 and its password).");
        }
    }
}
