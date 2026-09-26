using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using AuthCenter.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// AuthCenter with SAML configured and its "Federation" HTTP client answered by in-process fake
/// OpenID Connect providers, so federated sign-ins run end to end without a network.
/// </summary>
public sealed class FederationAuthCenterFactory : HttpsAuthCenterFactory
{
    public const string SamlEntityId = Authority + "/saml";
    public const string AcsUrl = Authority + "/api/federation/saml/acs";
    public const string OidcCallbackUrl = Authority + "/api/federation/oidc/callback";
    private const string SamlCertificatePassword = "saml-test-password";

    public FakeIdentityProviders IdentityProviders { get; } = new();

    /// <summary>AuthCenter's own SAML certificate: signs AuthnRequests and decrypts assertions.</summary>
    public X509Certificate2 ServiceProviderCertificate { get; } = TestCertificates.Create("CN=AuthCenter SAML service provider (tests)");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Saml:EntityId"] = SamlEntityId,
            ["Saml:AssertionConsumerServiceUrl"] = AcsUrl,
            ["Saml:SigningCertificateBase64"] = Convert.ToBase64String(ServiceProviderCertificate.Export(X509ContentType.Pkcs12, SamlCertificatePassword)),
            ["Saml:SigningCertificatePassword"] = SamlCertificatePassword
        }));
        builder.ConfigureServices(services => services
            .AddHttpClient(FederationMetadataCache.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new FakeIdentityProviderHandler(IdentityProviders)));
    }
}

public static class TestCertificates
{
    public static X509Certificate2 Create(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(365));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, "x"), "x", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }
}

public sealed class FakeIdentityProviders
{
    private readonly ConcurrentDictionary<string, FakeOidcProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public FakeOidcProvider CreateOidc()
    {
        var provider = new FakeOidcProvider($"idp-{Guid.NewGuid():N}.test");
        _providers[provider.Host] = provider;
        return provider;
    }

    public FakeOidcProvider? Find(string host) => _providers.GetValueOrDefault(host);
}

public sealed class FakeIdentityProviderHandler(FakeIdentityProviders providers) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        providers.Find(request.RequestUri!.Host) is { } provider
            ? await provider.HandleAsync(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
}

/// <summary>The identity a fake upstream provider asserts.</summary>
public sealed record FakeUpstreamUser(
    string Subject,
    string Email,
    string Name = "Federated User",
    bool? EmailVerified = true,
    string[]? Amr = null,
    string[]? Groups = null);

/// <summary>A minimal OpenID Connect provider: discovery, JWKS, code + PKCE token endpoint.</summary>
public sealed class FakeOidcProvider
{
    private readonly RSAParameters _key;
    private readonly string _keyId = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, IssuedCode> _codes = new(StringComparer.Ordinal);

    public FakeOidcProvider(string host)
    {
        Host = host;
        Issuer = BaseUrl;
        using var rsa = RSA.Create(2048);
        _key = rsa.ExportParameters(true);
    }

    public string Host { get; }
    public string BaseUrl => $"https://{Host}";

    /// <summary>The <c>iss</c> of the discovery document and the ID tokens.</summary>
    public string Issuer { get; set; }
    public string ClientId { get; } = "authcenter-federation";
    public string ClientSecret { get; } = "upstream-client-secret-for-federation-tests";
    public int TokenRequests { get; private set; }

    public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/.well-known/openid-configuration")
        {
            return Json(new Dictionary<string, object>
            {
                ["issuer"] = Issuer,
                ["authorization_endpoint"] = $"{BaseUrl}/authorize",
                ["token_endpoint"] = $"{BaseUrl}/token",
                ["jwks_uri"] = $"{BaseUrl}/jwks",
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["code_challenge_methods_supported"] = new[] { "S256" },
                ["scopes_supported"] = new[] { "openid", "profile", "email" },
                ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_post", "client_secret_basic" }
            });
        }
        if (request.Method == HttpMethod.Get && path == "/jwks")
        {
            return Json(new Dictionary<string, object>
            {
                ["keys"] = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["kty"] = "RSA", ["use"] = "sig", ["alg"] = "RS256", ["kid"] = _keyId,
                        ["n"] = Base64UrlEncoder.Encode(_key.Modulus!), ["e"] = Base64UrlEncoder.Encode(_key.Exponent!)
                    }
                }
            });
        }
        if (request.Method == HttpMethod.Post && path == "/token")
        {
            TokenRequests++;
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync());
            string Value(string name) => form.TryGetValue(name, out var value) ? value.ToString() : string.Empty;
            if (Value("grant_type") != "authorization_code" || !_codes.TryRemove(Value("code"), out var issued) ||
                Value("client_id") != ClientId || Value("client_secret") != ClientSecret || Value("redirect_uri") != issued.RedirectUri ||
                Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(Value("code_verifier")))) != issued.CodeChallenge)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json") };
            }
            return Json(new Dictionary<string, object>
            {
                ["access_token"] = "upstream-access-token",
                ["token_type"] = "Bearer",
                ["expires_in"] = 300,
                ["id_token"] = CreateIdToken(issued)
            });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>The user signs in at the provider: returns AuthCenter's callback URL with a code.</summary>
    public string SignIn(string authorizationUrl, FakeUpstreamUser user)
    {
        var uri = new Uri(authorizationUrl);
        Assert.Equal(Host, uri.Host);
        Assert.Equal("/authorize", uri.AbsolutePath);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(ClientId, query["client_id"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        var code = Guid.NewGuid().ToString("N");
        _codes[code] = new IssuedCode(query["redirect_uri"].ToString(), query["code_challenge"].ToString(), query["nonce"].ToString(), user);
        return QueryHelpers.AddQueryString(query["redirect_uri"].ToString(), new Dictionary<string, string?> { ["code"] = code, ["state"] = query["state"].ToString() });
    }

    private string CreateIdToken(IssuedCode issued)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = issued.User.Subject,
            ["nonce"] = issued.Nonce,
            ["email"] = issued.User.Email,
            ["name"] = issued.User.Name
        };
        if (issued.User.EmailVerified is { } verified) claims["email_verified"] = verified;
        if (issued.User.Amr is { } amr) claims["amr"] = amr;
        if (issued.User.Groups is { } groups) claims["groups"] = groups;
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_key) { KeyId = _keyId }, SecurityAlgorithms.RsaSha256)
        });
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private sealed record IssuedCode(string RedirectUri, string CodeChallenge, string Nonce, FakeUpstreamUser User);
}

/// <summary>A SAML identity provider that answers AuthCenter's AuthnRequests with signed (optionally encrypted) assertions.</summary>
public sealed class FakeSamlProvider
{
    private const string Protocol = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string Assertion = "urn:oasis:names:tc:SAML:2.0:assertion";

    public FakeSamlProvider(string entityId)
    {
        EntityId = entityId;
        Certificate = TestCertificates.Create("CN=Fake SAML identity provider");
    }

    public string EntityId { get; }
    public string SingleSignOnUrl { get; } = $"https://saml-{Guid.NewGuid():N}.test/sso";
    public X509Certificate2 Certificate { get; }
    public string CertificatePem => Certificate.ExportCertificatePem();

    /// <summary>Reads the AuthnRequest of AuthCenter's redirect: its ID, RelayState and ForceAuthn.</summary>
    public static (string RequestId, string RelayState, bool ForceAuthn) ReadRequest(string redirectUrl)
    {
        var query = QueryHelpers.ParseQuery(new Uri(redirectUrl).Query);
        using var inflater = new DeflateStream(new MemoryStream(Convert.FromBase64String(query["SAMLRequest"].ToString())), CompressionMode.Decompress);
        using var reader = new StreamReader(inflater, Encoding.UTF8);
        var document = new XmlDocument();
        document.LoadXml(reader.ReadToEnd());
        Assert.False(string.IsNullOrEmpty(query["Signature"].ToString()));
        return (document.DocumentElement!.GetAttribute("ID"), query["RelayState"].ToString(), document.DocumentElement.GetAttribute("ForceAuthn") == "true");
    }

    /// <summary>
    /// Builds the base64 SAMLResponse. The assertion is signed (SHA-256); the Response itself is not,
    /// like the default of Entra ID and AD FS. With an encryption certificate the assertion is
    /// encrypted to it (RSA-OAEP + AES-256-CBC).
    /// </summary>
    public string CreateResponse(
        string requestId,
        string subject,
        string email,
        string[]? groups = null,
        string? authnContextClass = null,
        X509Certificate2? encryptFor = null,
        Func<string, string>? tamperSignedAssertion = null)
    {
        var now = DateTime.UtcNow;
        string Instant(DateTime value) => XmlConvert.ToString(value, XmlDateTimeSerializationMode.Utc);
        var groupValues = string.Concat((groups ?? []).Select(group => $"<saml:AttributeValue>{Escape(group)}</saml:AttributeValue>"));
        var assertionXml =
            $"<saml:Assertion xmlns:saml=\"{Assertion}\" ID=\"_a{Guid.NewGuid():N}\" Version=\"2.0\" IssueInstant=\"{Instant(now)}\">" +
            $"<saml:Issuer>{Escape(EntityId)}</saml:Issuer>" +
            "<saml:Subject>" +
            $"<saml:NameID Format=\"urn:oasis:names:tc:SAML:2.0:nameid-format:persistent\">{Escape(subject)}</saml:NameID>" +
            "<saml:SubjectConfirmation Method=\"urn:oasis:names:tc:SAML:2.0:cm:bearer\">" +
            $"<saml:SubjectConfirmationData InResponseTo=\"{requestId}\" Recipient=\"{FederationAuthCenterFactory.AcsUrl}\" NotOnOrAfter=\"{Instant(now.AddMinutes(5))}\"/>" +
            "</saml:SubjectConfirmation></saml:Subject>" +
            $"<saml:Conditions NotBefore=\"{Instant(now.AddMinutes(-1))}\" NotOnOrAfter=\"{Instant(now.AddMinutes(5))}\">" +
            $"<saml:AudienceRestriction><saml:Audience>{FederationAuthCenterFactory.SamlEntityId}</saml:Audience></saml:AudienceRestriction></saml:Conditions>" +
            $"<saml:AuthnStatement AuthnInstant=\"{Instant(now)}\"><saml:AuthnContext><saml:AuthnContextClassRef>{authnContextClass ?? "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport"}</saml:AuthnContextClassRef></saml:AuthnContext></saml:AuthnStatement>" +
            "<saml:AttributeStatement>" +
            $"<saml:Attribute Name=\"http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress\"><saml:AttributeValue>{Escape(email)}</saml:AttributeValue></saml:Attribute>" +
            "<saml:Attribute Name=\"displayName\"><saml:AttributeValue>SAML User</saml:AttributeValue></saml:Attribute>" +
            (groups is null ? string.Empty : $"<saml:Attribute Name=\"groups\">{groupValues}</saml:Attribute>") +
            "</saml:AttributeStatement></saml:Assertion>";

        var signed = SignAssertion(assertionXml);
        if (tamperSignedAssertion is not null) signed = tamperSignedAssertion(signed);
        var assertionElement = encryptFor is null ? signed : Encrypt(signed, encryptFor);
        var response =
            $"<samlp:Response xmlns:samlp=\"{Protocol}\" xmlns:saml=\"{Assertion}\" ID=\"_r{Guid.NewGuid():N}\" Version=\"2.0\" IssueInstant=\"{Instant(now)}\" " +
            $"Destination=\"{FederationAuthCenterFactory.AcsUrl}\" InResponseTo=\"{requestId}\">" +
            $"<saml:Issuer>{Escape(EntityId)}</saml:Issuer>" +
            "<samlp:Status><samlp:StatusCode Value=\"urn:oasis:names:tc:SAML:2.0:status:Success\"/></samlp:Status>" +
            assertionElement +
            "</samlp:Response>";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(response));
    }

    private string SignAssertion(string assertionXml)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(assertionXml);
        var root = document.DocumentElement!;
        using var key = Certificate.GetRSAPrivateKey()!;
        var signedXml = new SignedXml(document) { SigningKey = key };
        signedXml.SignedInfo!.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        signedXml.SignedInfo.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        var reference = new Reference($"#{root.GetAttribute("ID")}") { DigestMethod = SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(Certificate));
        signedXml.KeyInfo = keyInfo;
        signedXml.ComputeSignature();
        // saml:Signature follows saml:Issuer in the assertion schema.
        root.InsertAfter(document.ImportNode(signedXml.GetXml(), true), root.FirstChild);
        return root.OuterXml;
    }

    private static string Encrypt(string assertionXml, X509Certificate2 recipient)
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.GenerateKey();
        aes.GenerateIV();
        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(assertionXml), aes.IV, PaddingMode.PKCS7);
        using var rsa = recipient.GetRSAPublicKey()!;
        var wrappedKey = rsa.Encrypt(aes.Key, RSAEncryptionPadding.OaepSHA1);
        return
            $"<saml:EncryptedAssertion><xenc:EncryptedData xmlns:xenc=\"http://www.w3.org/2001/04/xmlenc#\" Type=\"http://www.w3.org/2001/04/xmlenc#Element\">" +
            "<xenc:EncryptionMethod Algorithm=\"http://www.w3.org/2001/04/xmlenc#aes256-cbc\"/>" +
            "<ds:KeyInfo xmlns:ds=\"http://www.w3.org/2000/09/xmldsig#\"><xenc:EncryptedKey>" +
            "<xenc:EncryptionMethod Algorithm=\"http://www.w3.org/2001/04/xmlenc#rsa-oaep-mgf1p\"><ds:DigestMethod Algorithm=\"http://www.w3.org/2000/09/xmldsig#sha1\"/></xenc:EncryptionMethod>" +
            $"<xenc:CipherData><xenc:CipherValue>{Convert.ToBase64String(wrappedKey)}</xenc:CipherValue></xenc:CipherData>" +
            "</xenc:EncryptedKey></ds:KeyInfo>" +
            $"<xenc:CipherData><xenc:CipherValue>{Convert.ToBase64String([.. aes.IV, .. cipher])}</xenc:CipherValue></xenc:CipherData>" +
            "</xenc:EncryptedData></saml:EncryptedAssertion>";
    }

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
}
