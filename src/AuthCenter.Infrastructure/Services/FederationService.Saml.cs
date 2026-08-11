using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Federation;
using AuthCenter.Contracts.Responses.Auth;
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

    public async Task<OperationResult<SamlFederationChallengeResponse>> BeginSamlAsync(BeginSamlFederationRequest request, CancellationToken ct = default)
    {
        var provider = await _db.FederationProviders.AsNoTracking().FirstOrDefaultAsync(item => item.Id == request.ProviderId && item.IsActive && item.Protocol == FederationProtocol.Saml2, ct);
        if (provider is null) return OperationResult<SamlFederationChallengeResponse>.Failure("FEDERATION_PROVIDER_NOT_FOUND", "Active SAML provider not found.");
        if (!TryLocalSigningCertificate(out var signingCertificate) || string.IsNullOrWhiteSpace(provider.SamlSingleSignOnUrl))
            return OperationResult<SamlFederationChallengeResponse>.Failure("SAML_NOT_CONFIGURED", "SAML signing certificate and endpoints are not configured.");

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
        var rsa = signingCertificate!.GetRSAPrivateKey();
        if (rsa is null) return OperationResult<SamlFederationChallengeResponse>.Failure("SAML_NOT_CONFIGURED", "SAML signing certificate has no RSA private key.");
        var signature = Convert.ToBase64String(rsa.SignData(Encoding.ASCII.GetBytes(signedQuery), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        await _state.SetAsync(SamlStatePurpose, relayState, $"{provider.Id:N}|{requestId}", _clock.UtcNow.AddMinutes(5), ct);
        return OperationResult<SamlFederationChallengeResponse>.Success(new SamlFederationChallengeResponse
        {
            RedirectUrl = $"{provider.SamlSingleSignOnUrl}{(provider.SamlSingleSignOnUrl.Contains('?') ? '&' : '?')}{signedQuery}&Signature={Uri.EscapeDataString(signature)}",
            ExpiresIn = 300
        });
    }

    public async Task<OperationResult<AuthResponse>> CompleteSamlAsync(CompleteSamlFederationRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var expected = await _state.TakeAsync(SamlStatePurpose, request.RelayState, ct);
        var parts = expected?.Split('|', 2);
        if (parts is not { Length: 2 } || !Guid.TryParseExact(parts[0], "N", out var providerId))
            return OperationResult<AuthResponse>.Failure("INVALID_SAML_STATE", "SAML interaction is invalid, expired, or already used.");
        var provider = await _db.FederationProviders.Include(item => item.ApplicationSystem).FirstOrDefaultAsync(item => item.Id == providerId && item.IsActive && item.Protocol == FederationProtocol.Saml2, ct);
        if (provider is null || !TryCertificate(provider.SamlSigningCertificatePem, out var remoteCertificate))
            return OperationResult<AuthResponse>.Failure("FEDERATION_PROVIDER_NOT_FOUND", "SAML provider or signing certificate is unavailable.");

        XmlDocument document;
        try
        {
            if (request.SamlResponse.Length > 500_000) throw new FormatException();
            var bytes = Convert.FromBase64String(request.SamlResponse);
            if (bytes.Length > 350_000) throw new FormatException();
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 350_000 };
            using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
            document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            document.Load(reader);
        }
        catch (Exception exception) when (exception is FormatException or XmlException)
        {
            return OperationResult<AuthResponse>.Failure("INVALID_SAML_RESPONSE", "SAML response is malformed or exceeds the size limit.");
        }

        var root = document.DocumentElement;
        if (root?.LocalName != "Response" || root.NamespaceURI != SamlProtocolNamespace)
            return OperationResult<AuthResponse>.Failure("INVALID_SAML_RESPONSE", "SAML Response root element is required.");
        var responseId = root.GetAttribute("ID");
        if (string.IsNullOrWhiteSpace(responseId) || FindById(document, responseId).Count != 1 || root.GetAttribute("InResponseTo") != parts[1] || root.GetAttribute("Destination") != _samlSettings.AssertionConsumerServiceUrl)
            return OperationResult<AuthResponse>.Failure("INVALID_SAML_RESPONSE", "SAML response correlation or destination is invalid.");

        var ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("samlp", SamlProtocolNamespace); ns.AddNamespace("saml", SamlAssertionNamespace); ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
        var issuer = root.SelectSingleNode("./saml:Issuer", ns)?.InnerText;
        var status = root.SelectSingleNode("./samlp:Status/samlp:StatusCode", ns) as XmlElement;
        if (!string.Equals(issuer, provider.Issuer, StringComparison.Ordinal) || status?.GetAttribute("Value") != "urn:oasis:names:tc:SAML:2.0:status:Success")
            return OperationResult<AuthResponse>.Failure("INVALID_SAML_RESPONSE", "SAML issuer or status is invalid.");
        if (!ValidateResponseSignature(document, root, remoteCertificate!))
            return OperationResult<AuthResponse>.Failure("INVALID_SAML_SIGNATURE", "SAML response signature is missing, ambiguous, or invalid.");

        var assertions = root.SelectNodes("./saml:Assertion", ns);
        if (assertions?.Count != 1 || assertions[0] is not XmlElement assertion)
            return OperationResult<AuthResponse>.Failure("INVALID_SAML_ASSERTION", "Exactly one signed SAML assertion is required.");
        var now = _clock.UtcNow;
        var skew = TimeSpan.FromSeconds(_samlSettings.ClockSkewSeconds);
        var conditions = assertion.SelectSingleNode("./saml:Conditions", ns) as XmlElement;
        if (conditions is null || !WithinWindow(conditions.GetAttribute("NotBefore"), conditions.GetAttribute("NotOnOrAfter"), now, skew) ||
            !assertion.SelectNodes("./saml:Conditions/saml:AudienceRestriction/saml:Audience", ns)!.Cast<XmlNode>().Any(item => item.InnerText == _samlSettings.EntityId))
            return OperationResult<AuthResponse>.Failure("INVALID_SAML_CONDITIONS", "SAML assertion lifetime or audience is invalid.");
        var confirmation = assertion.SelectSingleNode("./saml:Subject/saml:SubjectConfirmation/saml:SubjectConfirmationData", ns) as XmlElement;
        if (confirmation is null || confirmation.GetAttribute("Recipient") != _samlSettings.AssertionConsumerServiceUrl || confirmation.GetAttribute("InResponseTo") != parts[1] || !NotExpired(confirmation.GetAttribute("NotOnOrAfter"), now, skew))
            return OperationResult<AuthResponse>.Failure("INVALID_SAML_SUBJECT", "SAML subject confirmation is invalid.");
        if (!await _state.TryConsumeAsync(SamlReplayPurpose, responseId, now.AddMinutes(10), ct))
            return OperationResult<AuthResponse>.Failure("SAML_REPLAY_DETECTED", "SAML response was already consumed.");

        var subject = assertion.SelectSingleNode("./saml:Subject/saml:NameID", ns)?.InnerText;
        var attributes = assertion.SelectNodes("./saml:AttributeStatement/saml:Attribute", ns)!.Cast<XmlElement>();
        var email = Attribute(attributes, "email") ?? Attribute(attributes, "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress") ?? subject;
        var name = Attribute(attributes, "name") ?? email;
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email)) return OperationResult<AuthResponse>.Failure("SAML_CLAIMS_INCOMPLETE", "SAML subject and email are required.");
        var userResult = await ResolveFederatedUserAsync(provider, subject, email, name ?? email, ct);
        if (!userResult.IsSuccess) return OperationResult<AuthResponse>.Failure(userResult.ErrorCode!, userResult.Message!);
        await _audit.LogAsync("FEDERATION_LOGIN_SUCCESS", userResult.Data!.Id, provider.ApplicationSystem.Code, nameof(FederationProvider), provider.Id.ToString(), ipAddress, userAgent, new { protocol = "SAML2" }, ct);
        return await _sessions.IssueAsync(userResult.Data, provider.ApplicationSystemId, provider.ApplicationSystem.Code, ipAddress, userAgent, ct: ct);
    }

    public async Task<OperationResult<string>> GetSamlMetadataAsync(Guid providerId, CancellationToken ct = default)
    {
        if (!await _db.FederationProviders.AsNoTracking().AnyAsync(item => item.Id == providerId && item.IsActive && item.Protocol == FederationProtocol.Saml2, ct) || !TryLocalSigningCertificate(out var certificate))
            return OperationResult<string>.Failure("SAML_NOT_CONFIGURED", "Active SAML provider and signing certificate are required.");
        var publicCertificate = Convert.ToBase64String(certificate!.Export(X509ContentType.Cert));
        var metadata = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{XmlEscape(_samlSettings.EntityId)}">
              <md:SPSSODescriptor AuthnRequestsSigned="true" WantAssertionsSigned="true" protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
                <md:KeyDescriptor use="signing"><ds:KeyInfo xmlns:ds="http://www.w3.org/2000/09/xmldsig#"><ds:X509Data><ds:X509Certificate>{publicCertificate}</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>
                <md:NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress</md:NameIDFormat>
                <md:AssertionConsumerService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="{XmlEscape(_samlSettings.AssertionConsumerServiceUrl)}" index="0" isDefault="true" />
              </md:SPSSODescriptor>
            </md:EntityDescriptor>
            """;
        return OperationResult<string>.Success(metadata);
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

    private static bool ValidateResponseSignature(XmlDocument document, XmlElement root, X509Certificate2 certificate)
    {
        var signatures = root.SelectNodes("./ds:Signature", CreateNs(document));
        if (signatures?.Count != 1 || signatures[0] is not XmlElement signature) return false;
        var signedXml = new SecureSignedXml(document); signedXml.LoadXml(signature);
        if (signedXml.SignedInfo is null || signedXml.SignedInfo.References.Count != 1 || signedXml.SignedInfo.References[0] is not Reference reference || reference.Uri != $"#{root.GetAttribute("ID")}") return false;
        var allowed = new HashSet<string>(StringComparer.Ordinal) { SignedXml.XmlDsigEnvelopedSignatureTransformUrl, SignedXml.XmlDsigExcC14NTransformUrl, SignedXml.XmlDsigC14NTransformUrl };
        for (var index = 0; index < reference.TransformChain.Count; index++)
            if (reference.TransformChain[index].Algorithm is not { } algorithm || !allowed.Contains(algorithm)) return false;
        return signedXml.CheckSignature(certificate, true);
    }

    private static XmlNamespaceManager CreateNs(XmlDocument document) { var ns = new XmlNamespaceManager(document.NameTable); ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl); return ns; }
    private static bool WithinWindow(string notBefore, string notOnOrAfter, DateTime now, TimeSpan skew) => DateTime.TryParse(notBefore, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var from) && DateTime.TryParse(notOnOrAfter, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var until) && now + skew >= from.ToUniversalTime() && now - skew < until.ToUniversalTime();
    private static bool NotExpired(string notOnOrAfter, DateTime now, TimeSpan skew) => DateTime.TryParse(notOnOrAfter, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var until) && now - skew < until.ToUniversalTime();
    private static string? Attribute(IEnumerable<XmlElement> attributes, string name) => attributes.FirstOrDefault(item => string.Equals(item.GetAttribute("Name"), name, StringComparison.OrdinalIgnoreCase))?.FirstChild?.InnerText;
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
