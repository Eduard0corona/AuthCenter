using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Federation;
using AuthCenter.Contracts.Responses.Federation;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public sealed partial class FederationService
{
    private const string SamlStatePurpose = "federation_saml_request";
    private const string SamlReplayPurpose = "federation_saml_response";
    private const string SamlProtocolNamespace = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string SamlAssertionNamespace = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string XmlEnc11Namespace = "http://www.w3.org/2009/xmlenc11#";
    private const string BearerConfirmation = "urn:oasis:names:tc:SAML:2.0:cm:bearer";
    private const int MaximumSamlBytes = 350_000;

    private static readonly HashSet<string> AllowedSignatureMethods = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigRSASHA256Url, SignedXml.XmlDsigRSASHA384Url, SignedXml.XmlDsigRSASHA512Url
    };

    private static readonly HashSet<string> AllowedDigestMethods = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigSHA256Url, SignedXml.XmlDsigSHA384Url, SignedXml.XmlDsigSHA512Url
    };

    private static readonly HashSet<string> AllowedTransforms = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigEnvelopedSignatureTransformUrl, SignedXml.XmlDsigExcC14NTransformUrl, SignedXml.XmlDsigC14NTransformUrl
    };

    /// <summary>SAML authentication context classes that mean a multi-factor sign-in.</summary>
    private static readonly HashSet<string> MultiFactorContextClasses = new(StringComparer.Ordinal)
    {
        "http://schemas.microsoft.com/claims/multipleauthn",
        "https://refeds.org/profile/mfa",
        "urn:oasis:names:tc:SAML:2.0:ac:classes:MobileTwoFactorContract",
        "urn:oasis:names:tc:SAML:2.0:ac:classes:MobileTwoFactorUnregistered",
        "urn:oasis:names:tc:SAML:2.0:ac:classes:TimeSyncToken",
        "urn:oasis:names:tc:SAML:2.0:ac:classes:Smartcard",
        "urn:oasis:names:tc:SAML:2.0:ac:classes:SmartcardPKI"
    };

    private static readonly string[] EmailAttributes =
    [
        "email", "mail", "emailaddress", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress", "urn:oid:0.9.2342.19200300.100.1.3"
    ];

    private static readonly string[] NameAttributes =
    [
        "name", "displayName", "http://schemas.microsoft.com/identity/claims/displayname", "urn:oid:2.16.840.1.113730.3.1.241",
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"
    ];

    /// <summary>JSON API start; the browser still posts the SAML response to the ACS.</summary>
    public async Task<OperationResult<SamlFederationChallengeResponse>> BeginSamlAsync(BeginSamlFederationRequest request, CancellationToken ct = default)
    {
        var provider = await _db.FederationProviders.AsNoTracking().FirstOrDefaultAsync(item => item.Id == request.ProviderId && item.IsActive && item.Protocol == FederationProtocol.Saml2, ct);
        if (provider is null) return OperationResult<SamlFederationChallengeResponse>.Failure("FEDERATION_PROVIDER_NOT_FOUND", "Active SAML provider not found.");
        var redirect = await BuildSamlChallengeAsync(provider, new FederationTransaction { ProviderId = provider.Id }, forceAuthentication: false, ct);
        return redirect.IsSuccess
            ? OperationResult<SamlFederationChallengeResponse>.Success(new SamlFederationChallengeResponse { RedirectUrl = redirect.Data!, ExpiresIn = (int)UpstreamLifetime.TotalSeconds })
            : OperationResult<SamlFederationChallengeResponse>.Failure(redirect.ErrorCode!, redirect.Message!);
    }

    /// <summary>A signed AuthnRequest (HTTP-Redirect binding); the transaction is stored under the RelayState.</summary>
    private async Task<OperationResult<string>> BuildSamlChallengeAsync(FederationProvider provider, FederationTransaction transaction, bool forceAuthentication, CancellationToken ct)
    {
        if (!TryLocalSigningCertificate(out var signingCertificate) || string.IsNullOrWhiteSpace(provider.SamlSingleSignOnUrl))
            return OperationResult<string>.Failure("SAML_NOT_CONFIGURED", "SAML signing certificate and endpoints are not configured.");

        var requestId = $"_{Guid.NewGuid():N}";
        var relayState = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var document = new XmlDocument { PreserveWhitespace = true };
        var root = document.CreateElement("samlp", "AuthnRequest", SamlProtocolNamespace);
        root.SetAttribute("ID", requestId);
        root.SetAttribute("Version", "2.0");
        root.SetAttribute("IssueInstant", XmlConvert.ToString(_clock.UtcNow, XmlDateTimeSerializationMode.Utc));
        root.SetAttribute("Destination", provider.SamlSingleSignOnUrl);
        root.SetAttribute("AssertionConsumerServiceURL", _samlSettings.AssertionConsumerServiceUrl);
        root.SetAttribute("ProtocolBinding", "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST");
        // A request that needs a fresh sign-in must not be answered by the upstream's own session.
        if (forceAuthentication) root.SetAttribute("ForceAuthn", "true");
        var issuer = document.CreateElement("saml", "Issuer", SamlAssertionNamespace);
        issuer.InnerText = _samlSettings.EntityId;
        root.AppendChild(issuer);
        var nameIdPolicy = document.CreateElement("samlp", "NameIDPolicy", SamlProtocolNamespace);
        nameIdPolicy.SetAttribute("AllowCreate", "true");
        nameIdPolicy.SetAttribute("Format", "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress");
        root.AppendChild(nameIdPolicy);
        document.AppendChild(root);

        string encodedRequest;
        await using (var output = new MemoryStream())
        {
            await using (var compressor = new DeflateStream(output, CompressionLevel.SmallestSize, true))
                await compressor.WriteAsync(Encoding.UTF8.GetBytes(document.OuterXml), ct);
            encodedRequest = Convert.ToBase64String(output.ToArray());
        }
        var sigAlg = SignedXml.XmlDsigRSASHA256Url;
        var signedQuery = $"SAMLRequest={Uri.EscapeDataString(encodedRequest)}&RelayState={Uri.EscapeDataString(relayState)}&SigAlg={Uri.EscapeDataString(sigAlg)}";
        using var rsa = signingCertificate!.GetRSAPrivateKey();
        if (rsa is null) return OperationResult<string>.Failure("SAML_NOT_CONFIGURED", "SAML signing certificate has no RSA private key.");
        var signature = Convert.ToBase64String(rsa.SignData(Encoding.ASCII.GetBytes(signedQuery), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        await _state.SetAsync(SamlStatePurpose, relayState, JsonSerializer.Serialize(transaction with { RequestId = requestId }), _clock.UtcNow.Add(UpstreamLifetime), ct);
        return OperationResult<string>.Success($"{provider.SamlSingleSignOnUrl}{(provider.SamlSingleSignOnUrl.Contains('?') ? '&' : '?')}{signedQuery}&Signature={Uri.EscapeDataString(signature)}");
    }

    public async Task<FederationCompletion> CompleteSamlAsync(CompleteSamlFederationRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        // The ACS is always reached by a browser, so an unknown RelayState goes back to the hosted login.
        var transaction = IsStateValue(request.RelayState) ? ReadTransaction(await _state.TakeAsync(SamlStatePurpose, request.RelayState, ct)) : null;
        if (transaction?.RequestId is null)
            return new FederationCompletion(LoginErrorPath("FEDERATION_STATE_INVALID"), null);

        var outcome = await AuthenticateSamlAsync(transaction, request.SamlResponse, ipAddress, userAgent, ct);
        if (transaction.Hosted)
            return new FederationCompletion(await StoreResultAsync(transaction, outcome, ct), null);
        if (!outcome.IsSuccess)
            return new FederationCompletion(null, OperationResult<Contracts.Responses.Auth.AuthResponse>.Failure(outcome.ErrorCode!, outcome.Message!));
        var (user, provider, authentication, _) = outcome.Data!;
        return new FederationCompletion(null, await _auth.CompleteFederatedSignInAsync(user!.Id, provider.ApplicationSystem.Code, authentication, ipAddress, userAgent, ct));
    }

    public async Task<OperationResult<string>> GetSamlMetadataAsync(Guid providerId, CancellationToken ct = default)
    {
        if (!await _db.FederationProviders.AsNoTracking().AnyAsync(item => item.Id == providerId && item.IsActive && item.Protocol == FederationProtocol.Saml2, ct) || !TryLocalSigningCertificate(out var certificate))
            return OperationResult<string>.Failure("SAML_NOT_CONFIGURED", "Active SAML provider and signing certificate are required.");
        var publicCertificate = Convert.ToBase64String(certificate!.Export(X509ContentType.Cert));
        var keyInfo = $"""<ds:KeyInfo xmlns:ds="http://www.w3.org/2000/09/xmldsig#"><ds:X509Data><ds:X509Certificate>{publicCertificate}</ds:X509Certificate></ds:X509Data></ds:KeyInfo>""";
        var metadata = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{XmlEscape(_samlSettings.EntityId)}">
              <md:SPSSODescriptor AuthnRequestsSigned="true" WantAssertionsSigned="true" protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
                <md:KeyDescriptor use="signing">{keyInfo}</md:KeyDescriptor>
                <md:KeyDescriptor use="encryption">{keyInfo}
                  <md:EncryptionMethod Algorithm="http://www.w3.org/2009/xmlenc11#aes256-gcm" />
                  <md:EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes256-cbc" />
                  <md:EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#rsa-oaep-mgf1p" />
                </md:KeyDescriptor>
                <md:NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress</md:NameIDFormat>
                <md:AssertionConsumerService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="{XmlEscape(_samlSettings.AssertionConsumerServiceUrl)}" index="0" isDefault="true" />
              </md:SPSSODescriptor>
            </md:EntityDescriptor>
            """;
        return OperationResult<string>.Success(metadata);
    }

    private async Task<OperationResult<FederatedAuthentication>> AuthenticateSamlAsync(FederationTransaction transaction, string samlResponse, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        var provider = await _db.FederationProviders.Include(item => item.ApplicationSystem)
            .FirstOrDefaultAsync(item => item.Id == transaction.ProviderId && item.IsActive && item.Protocol == FederationProtocol.Saml2, ct);
        if (provider is null || !TryCertificate(provider.SamlSigningCertificatePem, out var remoteCertificate))
            return OperationResult<FederatedAuthentication>.Failure("FEDERATION_PROVIDER_NOT_FOUND", "SAML provider or signing certificate is unavailable.");
        var identity = await ReadSamlIdentityAsync(provider, remoteCertificate!, transaction.RequestId!, samlResponse, ct);
        return identity.IsSuccess
            ? await AuthenticateUpstreamIdentityAsync(provider, identity.Data!, transaction, "SAML2", ipAddress, userAgent, ct)
            : await FailAsync(provider, identity.ErrorCode!, identity.Message!, "SAML2", ipAddress, userAgent, ct);
    }

    /// <summary>
    /// Validates a SAML response to one of our requests. The Response, its assertion or both may be
    /// signed, every signature present must be valid, and the assertion may be encrypted to our
    /// certificate. Only the assertion that is a direct child of the Response (or the result of
    /// decrypting it) is read, and signatures must reference that exact element by a unique ID.
    /// </summary>
    private async Task<OperationResult<UpstreamIdentity>> ReadSamlIdentityAsync(FederationProvider provider, X509Certificate2 remoteCertificate, string requestId, string samlResponse, CancellationToken ct)
    {
        static OperationResult<UpstreamIdentity> Fail(string code, string message) => OperationResult<UpstreamIdentity>.Failure(code, message);

        XmlDocument document;
        try
        {
            if (string.IsNullOrWhiteSpace(samlResponse) || samlResponse.Length > 500_000) throw new FormatException();
            document = LoadSecureXml(Convert.FromBase64String(samlResponse));
        }
        catch (Exception exception) when (exception is FormatException or XmlException)
        {
            return Fail("INVALID_SAML_RESPONSE", "SAML response is malformed or exceeds the size limit.");
        }

        var root = document.DocumentElement;
        if (root?.LocalName != "Response" || root.NamespaceURI != SamlProtocolNamespace)
            return Fail("INVALID_SAML_RESPONSE", "SAML Response root element is required.");
        var responseId = root.GetAttribute("ID");
        if (string.IsNullOrWhiteSpace(responseId) || FindById(document, responseId).Count != 1 || root.GetAttribute("InResponseTo") != requestId || root.GetAttribute("Destination") != _samlSettings.AssertionConsumerServiceUrl)
            return Fail("INVALID_SAML_RESPONSE", "SAML response correlation or destination is invalid.");

        var ns = CreateNamespaces(document);
        var responseIssuer = root.SelectSingleNode("./saml:Issuer", ns)?.InnerText.Trim();
        var status = root.SelectSingleNode("./samlp:Status/samlp:StatusCode", ns) as XmlElement;
        if ((responseIssuer is not null && !string.Equals(responseIssuer, provider.Issuer, StringComparison.Ordinal)) || status?.GetAttribute("Value") != "urn:oasis:names:tc:SAML:2.0:status:Success")
            return Fail("INVALID_SAML_RESPONSE", "SAML issuer or status is invalid.");

        if (!TrySignatureOf(root, ns, out var responseSignature))
            return Fail("INVALID_SAML_SIGNATURE", "SAML response signature is ambiguous.");
        if (responseSignature is not null && !VerifySignature(document, root, responseSignature, remoteCertificate))
            return Fail("INVALID_SAML_SIGNATURE", "SAML response signature is invalid or uses a weak algorithm.");

        var plain = root.SelectNodes("./saml:Assertion", ns)!;
        var encrypted = root.SelectNodes("./saml:EncryptedAssertion", ns)!;
        if (plain.Count + encrypted.Count != 1)
            return Fail("INVALID_SAML_ASSERTION", "Exactly one SAML assertion is required.");
        XmlElement assertion;
        if (encrypted.Count == 1)
        {
            // Every decryption failure has the same answer, so it cannot serve as a padding oracle.
            if (!TryDecryptAssertion((XmlElement)encrypted[0]!, out var decrypted))
                return Fail("INVALID_SAML_ASSERTION", "The encrypted SAML assertion could not be decrypted.");
            assertion = decrypted!;
        }
        else
        {
            assertion = (XmlElement)plain[0]!;
        }
        var assertionDocument = assertion.OwnerDocument;
        var assertionNs = CreateNamespaces(assertionDocument);
        var assertionId = assertion.GetAttribute("ID");
        if (string.IsNullOrWhiteSpace(assertionId) || FindById(assertionDocument, assertionId).Count != 1)
            return Fail("INVALID_SAML_ASSERTION", "The SAML assertion needs a unique ID.");
        if (!TrySignatureOf(assertion, assertionNs, out var assertionSignature))
            return Fail("INVALID_SAML_SIGNATURE", "SAML assertion signature is ambiguous.");
        if (assertionSignature is not null && !VerifySignature(assertionDocument, assertion, assertionSignature, remoteCertificate))
            return Fail("INVALID_SAML_SIGNATURE", "SAML assertion signature is invalid or uses a weak algorithm.");
        if (responseSignature is null && assertionSignature is null)
            return Fail("INVALID_SAML_SIGNATURE", "The SAML response or its assertion must be signed.");
        if (!string.Equals(assertion.SelectSingleNode("./saml:Issuer", assertionNs)?.InnerText.Trim(), provider.Issuer, StringComparison.Ordinal))
            return Fail("INVALID_SAML_ASSERTION", "The SAML assertion issuer is not this provider.");

        var now = _clock.UtcNow;
        var skew = TimeSpan.FromSeconds(_samlSettings.ClockSkewSeconds);
        var conditions = assertion.SelectSingleNode("./saml:Conditions", assertionNs) as XmlElement;
        if (conditions is null || !WithinWindow(conditions.GetAttribute("NotBefore"), conditions.GetAttribute("NotOnOrAfter"), now, skew) ||
            !assertion.SelectNodes("./saml:Conditions/saml:AudienceRestriction/saml:Audience", assertionNs)!.Cast<XmlNode>().Any(item => item.InnerText.Trim() == _samlSettings.EntityId))
            return Fail("INVALID_SAML_CONDITIONS", "SAML assertion lifetime or audience is invalid.");
        var confirmation = assertion.SelectSingleNode($"./saml:Subject/saml:SubjectConfirmation[@Method='{BearerConfirmation}']/saml:SubjectConfirmationData", assertionNs) as XmlElement;
        if (confirmation is null || confirmation.GetAttribute("Recipient") != _samlSettings.AssertionConsumerServiceUrl || confirmation.GetAttribute("InResponseTo") != requestId || !NotExpired(confirmation.GetAttribute("NotOnOrAfter"), now, skew))
            return Fail("INVALID_SAML_SUBJECT", "SAML subject confirmation is invalid.");
        if (!await _state.TryConsumeAsync(SamlReplayPurpose, responseId, now.AddMinutes(10), ct) ||
            !await _state.TryConsumeAsync(SamlReplayPurpose, $"assertion:{assertionId}", now.AddMinutes(10), ct))
            return Fail("SAML_REPLAY_DETECTED", "SAML response was already consumed.");

        var subject = assertion.SelectSingleNode("./saml:Subject/saml:NameID", assertionNs)?.InnerText.Trim();
        var attributes = assertion.SelectNodes("./saml:AttributeStatement/saml:Attribute", assertionNs)!.Cast<XmlElement>().ToList();
        var email = FirstValue(attributes, EmailAttributes, assertionNs) ?? (subject is not null && subject.Contains('@', StringComparison.Ordinal) ? subject : null);
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
            return Fail("SAML_CLAIMS_INCOMPLETE", "SAML subject and email are required.");
        var contextClasses = assertion.SelectNodes("./saml:AuthnStatement/saml:AuthnContext/saml:AuthnContextClassRef", assertionNs)!.Cast<XmlNode>().Select(item => item.InnerText.Trim());
        var methodReferences = Values(attributes, "http://schemas.microsoft.com/claims/authnmethodsreferences", assertionNs);
        var multiFactor = contextClasses.Concat(methodReferences).Any(MultiFactorContextClasses.Contains);

        IReadOnlyCollection<string>? groups = null;
        // Entra ID replaces a long group list with a link ("overage"); memberships are then left untouched.
        if (!string.IsNullOrWhiteSpace(provider.GroupsClaim) && Values(attributes, "http://schemas.microsoft.com/claims/groups.link", assertionNs).Count == 0)
            groups = Values(attributes, provider.GroupsClaim, assertionNs);

        // The SAML provider is authoritative for the identities it asserts with its own certificate.
        return OperationResult<UpstreamIdentity>.Success(new UpstreamIdentity(subject, email, FirstValue(attributes, NameAttributes, assertionNs) ?? email, EmailVerified: true, multiFactor, groups));
    }

    private bool TryLocalSigningCertificate(out X509Certificate2? certificate)
    {
        certificate = null;
        try
        {
            if (string.IsNullOrWhiteSpace(_samlSettings.SigningCertificateBase64)) return false;
            certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(_samlSettings.SigningCertificateBase64), _samlSettings.SigningCertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            return certificate.HasPrivateKey && certificate.NotBefore.ToUniversalTime() <= _clock.UtcNow && certificate.NotAfter.ToUniversalTime() > _clock.UtcNow;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException) { return false; }
    }

    /// <summary>
    /// Decrypts an EncryptedAssertion addressed to AuthCenter's certificate: RSA-OAEP key transport
    /// (RSA 1.5 is refused) and AES-CBC or AES-GCM content encryption.
    /// </summary>
    private bool TryDecryptAssertion(XmlElement encryptedAssertion, out XmlElement? assertion)
    {
        assertion = null;
        if (!TryLocalSigningCertificate(out var certificate))
            return false;
        using var rsa = certificate!.GetRSAPrivateKey();
        if (rsa is null)
            return false;

        var ns = new XmlNamespaceManager(encryptedAssertion.OwnerDocument.NameTable);
        ns.AddNamespace("xenc", EncryptedXml.XmlEncNamespaceUrl);
        ns.AddNamespace("xenc11", XmlEnc11Namespace);
        ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
        var encryptedData = encryptedAssertion.SelectSingleNode("./xenc:EncryptedData", ns) as XmlElement;
        var dataAlgorithm = (encryptedData?.SelectSingleNode("./xenc:EncryptionMethod", ns) as XmlElement)?.GetAttribute("Algorithm");
        var encryptedKey = encryptedData?.SelectSingleNode("./ds:KeyInfo/xenc:EncryptedKey", ns) as XmlElement
            ?? encryptedAssertion.SelectSingleNode("./xenc:EncryptedKey", ns) as XmlElement;
        var keyMethod = encryptedKey?.SelectSingleNode("./xenc:EncryptionMethod", ns) as XmlElement;
        var wrappedKey = encryptedKey?.SelectSingleNode("./xenc:CipherData/xenc:CipherValue", ns)?.InnerText;
        var cipherText = encryptedData?.SelectSingleNode("./xenc:CipherData/xenc:CipherValue", ns)?.InnerText;
        if (dataAlgorithm is null || keyMethod is null || wrappedKey is null || cipherText is null || OaepPadding(keyMethod, ns) is not { } padding)
            return false;
        try
        {
            var key = rsa.Decrypt(Convert.FromBase64String(wrappedKey), padding);
            var plaintext = DecryptContent(dataAlgorithm, key, Convert.FromBase64String(cipherText));
            if (plaintext is null)
                return false;
            var decrypted = LoadSecureXml(plaintext);
            if (decrypted.DocumentElement is not { LocalName: "Assertion", NamespaceURI: SamlAssertionNamespace } root)
                return false;
            assertion = root;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or XmlException or ArgumentException)
        {
            return false;
        }
    }

    private static RSAEncryptionPadding? OaepPadding(XmlElement method, XmlNamespaceManager ns)
    {
        static HashAlgorithmName? Hash(string? algorithm) => algorithm switch
        {
            null or SignedXml.XmlDsigSHA1Url or XmlEnc11Namespace + "mgf1sha1" => HashAlgorithmName.SHA1,
            SignedXml.XmlDsigSHA256Url or XmlEnc11Namespace + "mgf1sha256" => HashAlgorithmName.SHA256,
            SignedXml.XmlDsigSHA384Url or XmlEnc11Namespace + "mgf1sha384" => HashAlgorithmName.SHA384,
            SignedXml.XmlDsigSHA512Url or XmlEnc11Namespace + "mgf1sha512" => HashAlgorithmName.SHA512,
            _ => null
        };

        var digest = Hash((method.SelectSingleNode("./ds:DigestMethod", ns) as XmlElement)?.GetAttribute("Algorithm"));
        var mgf = method.GetAttribute("Algorithm") switch
        {
            "http://www.w3.org/2001/04/xmlenc#rsa-oaep-mgf1p" => HashAlgorithmName.SHA1,
            XmlEnc11Namespace + "rsa-oaep" => Hash((method.SelectSingleNode("./xenc11:MGF", ns) as XmlElement)?.GetAttribute("Algorithm")),
            _ => null
        };
        // .NET's OAEP uses one hash for both the digest and MGF1.
        return digest is { } hash && mgf == hash ? RSAEncryptionPadding.CreateOaep(hash) : null;
    }

    private static byte[]? DecryptContent(string algorithm, byte[] key, byte[] cipher)
    {
        var (gcm, keyLength) = algorithm switch
        {
            "http://www.w3.org/2001/04/xmlenc#aes128-cbc" => (false, 16),
            "http://www.w3.org/2001/04/xmlenc#aes192-cbc" => (false, 24),
            "http://www.w3.org/2001/04/xmlenc#aes256-cbc" => (false, 32),
            XmlEnc11Namespace + "aes128-gcm" => (true, 16),
            XmlEnc11Namespace + "aes192-gcm" => (true, 24),
            XmlEnc11Namespace + "aes256-gcm" => (true, 32),
            _ => (false, 0)
        };
        if (keyLength == 0 || key.Length != keyLength)
            return null;
        if (!gcm)
        {
            if (cipher.Length < 32 || cipher.Length % 16 != 0)
                return null;
            using var aes = Aes.Create();
            aes.Key = key;
            return aes.DecryptCbc(cipher.AsSpan(16), cipher.AsSpan(0, 16), PaddingMode.ISO10126);
        }
        if (cipher.Length < 12 + 16)
            return null;
        var plaintext = new byte[cipher.Length - 28];
        using var aesGcm = new AesGcm(key, 16);
        aesGcm.Decrypt(cipher.AsSpan(0, 12), cipher.AsSpan(12, cipher.Length - 28), cipher.AsSpan(cipher.Length - 16), plaintext);
        return plaintext;
    }

    /// <summary>The element's own ds:Signature; <c>false</c> when there is more than one.</summary>
    private static bool TrySignatureOf(XmlElement element, XmlNamespaceManager ns, out XmlElement? signature)
    {
        var signatures = element.SelectNodes("./ds:Signature", ns)!;
        signature = signatures.Count == 1 ? signatures[0] as XmlElement : null;
        return signatures.Count <= 1;
    }

    private static bool VerifySignature(XmlDocument document, XmlElement signedElement, XmlElement signature, X509Certificate2 certificate)
    {
        try
        {
            var signedXml = new SecureSignedXml(document);
            signedXml.LoadXml(signature);
            if (signedXml.SignedInfo?.SignatureMethod is not { } signatureMethod || !AllowedSignatureMethods.Contains(signatureMethod) ||
                signedXml.SignedInfo.References.Count != 1 || signedXml.SignedInfo.References[0] is not Reference reference ||
                reference.Uri != $"#{signedElement.GetAttribute("ID")}" || reference.DigestMethod is not { } digestMethod || !AllowedDigestMethods.Contains(digestMethod))
                return false;
            for (var index = 0; index < reference.TransformChain.Count; index++)
                if (reference.TransformChain[index].Algorithm is not { } algorithm || !AllowedTransforms.Contains(algorithm)) return false;
            return signedXml.CheckSignature(certificate, true);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static XmlDocument LoadSecureXml(byte[] bytes)
    {
        if (bytes.Length > MaximumSamlBytes) throw new FormatException();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumSamlBytes };
        using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        document.Load(reader);
        return document;
    }

    private static XmlNamespaceManager CreateNamespaces(XmlDocument document)
    {
        var ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("samlp", SamlProtocolNamespace);
        ns.AddNamespace("saml", SamlAssertionNamespace);
        ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
        return ns;
    }

    private static bool WithinWindow(string notBefore, string notOnOrAfter, DateTime now, TimeSpan skew) =>
        (string.IsNullOrEmpty(notBefore) || (TryUtc(notBefore, out var from) && now + skew >= from)) && NotExpired(notOnOrAfter, now, skew);

    private static bool NotExpired(string notOnOrAfter, DateTime now, TimeSpan skew) => TryUtc(notOnOrAfter, out var until) && now - skew < until;

    private static bool TryUtc(string value, out DateTime instant)
    {
        instant = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { instant = XmlConvert.ToDateTime(value, XmlDateTimeSerializationMode.Utc); return true; }
        catch (FormatException) { return false; }
    }

    private static List<string> Values(IEnumerable<XmlElement> attributes, string name, XmlNamespaceManager ns) =>
        attributes.Where(item => string.Equals(item.GetAttribute("Name"), name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.SelectNodes("./saml:AttributeValue", ns)!.Cast<XmlNode>())
            .Select(item => item.InnerText.Trim())
            .Where(value => value.Length > 0)
            .ToList();

    private static string? FirstValue(IReadOnlyCollection<XmlElement> attributes, IEnumerable<string> names, XmlNamespaceManager ns) =>
        names.Select(name => Values(attributes, name, ns).FirstOrDefault()).FirstOrDefault(value => value is not null);

    private static List<XmlElement> FindById(XmlDocument document, string id) => document.SelectNodes("//*")!.Cast<XmlNode>().OfType<XmlElement>().Where(item => item.GetAttribute("ID") == id).ToList();
    private static string XmlEscape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private sealed class SecureSignedXml : SignedXml
    {
        private readonly XmlDocument _document;
        public SecureSignedXml(XmlDocument document) : base(document) => _document = document;
        public override XmlElement? GetIdElement(XmlDocument? document, string idValue)
        {
            var nodes = FindById(_document, idValue);
            return nodes.Count == 1 ? nodes[0] : null;
        }
    }
}
