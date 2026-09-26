using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace AuthCenter.Infrastructure.Services.Saml;

/// <summary>A request a service provider sent: an AuthnRequest or a LogoutRequest.</summary>
internal sealed record SamlInboundRequest(
    XmlDocument Document,
    string Kind,
    string Id,
    string? Issuer,
    DateTime? IssueInstant,
    string? Destination,
    string? AssertionConsumerServiceUrl,
    string? ProtocolBinding,
    string? NameIdPolicyFormat,
    bool ForceAuthn,
    bool IsPassive,
    string? NameId,
    string? SessionIndex,
    IReadOnlyList<string> RequestedAuthnContextClasses);

/// <summary>An attribute of an assertion, with one or more values.</summary>
internal sealed record SamlAttribute(string Name, IReadOnlyList<string> Values);

/// <summary>What an assertion says about the user and the authentication.</summary>
internal sealed record SamlAssertionContent(
    string NameId,
    string NameIdFormat,
    DateTime AuthenticatedAt,
    string SessionIndex,
    string AuthnContextClassRef,
    IReadOnlyList<SamlAttribute> Attributes);

/// <summary>
/// SAML 2.0 messages of the identity provider role: reading requests safely (no DTDs, bounded size,
/// HTTP-Redirect and HTTP-POST bindings), checking their signatures, and building signed (and
/// optionally encrypted) responses, logout responses and the IdP metadata.
/// </summary>
internal static class SamlIdpMessages
{
    public const string ProtocolNamespace = "urn:oasis:names:tc:SAML:2.0:protocol";
    public const string AssertionNamespace = "urn:oasis:names:tc:SAML:2.0:assertion";
    public const string MetadataNamespace = "urn:oasis:names:tc:SAML:2.0:metadata";
    public const string PostBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST";
    public const string RedirectBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect";
    public const string Success = "urn:oasis:names:tc:SAML:2.0:status:Success";
    public const string Requester = "urn:oasis:names:tc:SAML:2.0:status:Requester";
    public const string Responder = "urn:oasis:names:tc:SAML:2.0:status:Responder";
    public const string RequestDenied = "urn:oasis:names:tc:SAML:2.0:status:RequestDenied";
    public const string NoPassive = "urn:oasis:names:tc:SAML:2.0:status:NoPassive";
    public const string InvalidNameIdPolicy = "urn:oasis:names:tc:SAML:2.0:status:InvalidNameIDPolicy";
    public const string PasswordProtectedTransport = "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport";
    public const string MultiFactor = "https://refeds.org/profile/mfa";

    /// <summary>Authentication context classes that mean more than one factor (the same ones AuthCenter accepts from upstream IdPs).</summary>
    public static readonly IReadOnlySet<string> MultiFactorContextClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "http://schemas.microsoft.com/claims/multipleauthn",
        "https://refeds.org/profile/mfa",
        "urn:oasis:names:tc:SAML:2.0:ac:classes:MobileTwoFactorContract",
        "urn:oasis:names:tc:SAML:2.0:ac:classes:MobileTwoFactorUnregistered",
        "urn:oasis:names:tc:SAML:2.0:ac:classes:TimeSyncToken",
        "urn:oasis:names:tc:SAML:2.0:ac:classes:Smartcard",
        "urn:oasis:names:tc:SAML:2.0:ac:classes:SmartcardPKI"
    };
    private const string RsaSha256 = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
    private const string RsaSha512 = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha512";
    private const string Sha256 = "http://www.w3.org/2001/04/xmlenc#sha256";
    private const int MaxMessageBytes = 256 * 1024;

    /// <summary>A request of the HTTP-Redirect binding: base64 of the DEFLATE-compressed XML.</summary>
    public static SamlInboundRequest? ReadRedirect(string samlRequest)
    {
        try
        {
            var compressed = Convert.FromBase64String(samlRequest);
            using var input = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxMessageBytes)
                    return null;
            }
            return Read(output.ToArray());
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A request of the HTTP-POST binding: base64 of the XML.</summary>
    public static SamlInboundRequest? ReadPost(string samlRequest)
    {
        try
        {
            var bytes = Convert.FromBase64String(samlRequest);
            return bytes.Length > MaxMessageBytes ? null : Read(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static SamlInboundRequest? Read(byte[] xml)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxMessageBytes
            });
            document.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
        var root = document.DocumentElement;
        if (root is null || root.NamespaceURI != ProtocolNamespace || root.LocalName is not ("AuthnRequest" or "LogoutRequest"))
            return null;
        var id = root.GetAttribute("ID");
        if (string.IsNullOrWhiteSpace(id) || root.GetAttribute("Version") != "2.0")
            return null;
        var issuer = Child(root, AssertionNamespace, "Issuer")?.InnerText.Trim();
        var policy = Child(root, ProtocolNamespace, "NameIDPolicy");
        return new SamlInboundRequest(
            document,
            root.LocalName,
            id,
            issuer,
            DateTime.TryParse(root.GetAttribute("IssueInstant"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var instant) ? instant : null,
            NullIfEmpty(root.GetAttribute("Destination")),
            NullIfEmpty(root.GetAttribute("AssertionConsumerServiceURL")),
            NullIfEmpty(root.GetAttribute("ProtocolBinding")),
            policy is null ? null : NullIfEmpty(policy.GetAttribute("Format")),
            string.Equals(root.GetAttribute("ForceAuthn"), "true", StringComparison.OrdinalIgnoreCase),
            string.Equals(root.GetAttribute("IsPassive"), "true", StringComparison.OrdinalIgnoreCase),
            Child(root, AssertionNamespace, "NameID")?.InnerText.Trim() ?? SubjectNameId(root),
            Child(root, ProtocolNamespace, "SessionIndex")?.InnerText.Trim(),
            Child(root, ProtocolNamespace, "RequestedAuthnContext") is { } requested
                ? requested.ChildNodes.OfType<XmlElement>().Where(element => element.NamespaceURI == AssertionNamespace && element.LocalName == "AuthnContextClassRef")
                    .Select(element => element.InnerText.Trim()).Where(value => value.Length > 0).ToList()
                : []);
    }

    /// <summary>
    /// The signature of the HTTP-Redirect binding: over the query parameters exactly as they were
    /// sent (SAMLRequest, RelayState, SigAlg), RSA with SHA-256 or SHA-512.
    /// </summary>
    public static bool VerifyRedirectSignature(string rawQuery, X509Certificate2 certificate)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0)
                parameters.TryAdd(part[..separator], part[(separator + 1)..]);
        }
        if (!parameters.TryGetValue("SAMLRequest", out var request) || !parameters.TryGetValue("SigAlg", out var algorithm) || !parameters.TryGetValue("Signature", out var signature))
            return false;
        var hash = Uri.UnescapeDataString(algorithm) switch
        {
            RsaSha256 => HashAlgorithmName.SHA256,
            RsaSha512 => HashAlgorithmName.SHA512,
            _ => (HashAlgorithmName?)null
        };
        if (hash is null)
            return false;
        var signed = $"SAMLRequest={request}" + (parameters.TryGetValue("RelayState", out var relayState) ? $"&RelayState={relayState}" : string.Empty) + $"&SigAlg={algorithm}";
        try
        {
            using var key = certificate.GetRSAPublicKey();
            return key is not null && key.VerifyData(Encoding.UTF8.GetBytes(signed), Convert.FromBase64String(Uri.UnescapeDataString(signature)), hash.Value, RSASignaturePadding.Pkcs1);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The enveloped XML signature of a request of the HTTP-POST binding. Only a signature over the
    /// whole request counts (no wrapping), with the enveloped and exclusive canonicalization transforms.
    /// </summary>
    public static bool VerifyXmlSignature(XmlDocument document, X509Certificate2 certificate)
    {
        var root = document.DocumentElement!;
        var signatures = root.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
        if (signatures.Count != 1 || signatures[0]!.ParentNode != root)
            return false;
        var signedXml = new SignedXml(root);
        signedXml.LoadXml((XmlElement)signatures[0]!);
        if (signedXml.SignedInfo?.References.Count != 1 || signedXml.SignedInfo.References[0] is not Reference reference ||
            reference.Uri != "#" + root.GetAttribute("ID") || signedXml.SignatureMethod is not (RsaSha256 or RsaSha512))
            return false;
        for (var index = 0; index < reference.TransformChain.Count; index++)
            if (reference.TransformChain[index] is not (XmlDsigEnvelopedSignatureTransform or XmlDsigExcC14NTransform))
                return false;
        return signedXml.CheckSignature(certificate, verifySignatureOnly: true);
    }

    /// <summary>A response to an AuthnRequest: success with the assertion, or a failure status.</summary>
    public static string BuildResponse(
        string issuer, string destination, string? inResponseTo, string audience, DateTime now, int lifetimeMinutes,
        SamlAssertionContent? content, (string Code, string? SubCode, string? Message)? failure,
        X509Certificate2 signingCertificate, bool signResponse, X509Certificate2? encryptionCertificate)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        var response = document.CreateElement("samlp", "Response", ProtocolNamespace);
        document.AppendChild(response);
        response.SetAttribute("xmlns:saml", AssertionNamespace);
        var responseId = NewId();
        response.SetAttribute("ID", responseId);
        response.SetAttribute("Version", "2.0");
        response.SetAttribute("IssueInstant", Instant(now));
        response.SetAttribute("Destination", destination);
        if (inResponseTo is not null)
            response.SetAttribute("InResponseTo", inResponseTo);
        var responseIssuer = AppendText(response, "saml", "Issuer", AssertionNamespace, issuer);
        AppendStatus(response, failure);

        if (failure is null && content is not null)
        {
            var assertion = BuildAssertion(document, issuer, destination, inResponseTo, audience, now, lifetimeMinutes, content);
            response.AppendChild(assertion);
            Sign(document, assertion, assertion.GetAttribute("ID"), signingCertificate, (XmlElement)assertion.FirstChild!);
            if (encryptionCertificate is not null)
                Encrypt(document, assertion, encryptionCertificate);
        }
        if (signResponse || failure is not null)
            Sign(document, response, responseId, signingCertificate, responseIssuer);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(document.OuterXml));
    }

    /// <summary>The answer to a LogoutRequest, signed, for the HTTP-POST binding.</summary>
    public static string BuildLogoutResponse(string issuer, string destination, string inResponseTo, DateTime now, bool succeeded, X509Certificate2 signingCertificate)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        var response = document.CreateElement("samlp", "LogoutResponse", ProtocolNamespace);
        document.AppendChild(response);
        response.SetAttribute("xmlns:saml", AssertionNamespace);
        var id = NewId();
        response.SetAttribute("ID", id);
        response.SetAttribute("Version", "2.0");
        response.SetAttribute("IssueInstant", Instant(now));
        response.SetAttribute("Destination", destination);
        response.SetAttribute("InResponseTo", inResponseTo);
        var responseIssuer = AppendText(response, "saml", "Issuer", AssertionNamespace, issuer);
        AppendStatus(response, succeeded ? null : (Responder, null, "The session could not be ended."));
        Sign(document, response, id, signingCertificate, responseIssuer);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(document.OuterXml));
    }

    public static string BuildMetadata(string entityId, string singleSignOnUrl, string singleLogoutUrl, X509Certificate2 signingCertificate)
    {
        var document = new XmlDocument();
        document.AppendChild(document.CreateXmlDeclaration("1.0", "UTF-8", null));
        var descriptor = document.CreateElement("md", "EntityDescriptor", MetadataNamespace);
        descriptor.SetAttribute("entityID", entityId);
        document.AppendChild(descriptor);
        var idp = document.CreateElement("md", "IDPSSODescriptor", MetadataNamespace);
        idp.SetAttribute("WantAuthnRequestsSigned", "false");
        idp.SetAttribute("protocolSupportEnumeration", ProtocolNamespace);
        descriptor.AppendChild(idp);
        var key = document.CreateElement("md", "KeyDescriptor", MetadataNamespace);
        key.SetAttribute("use", "signing");
        var keyInfo = document.CreateElement("ds", "KeyInfo", SignedXml.XmlDsigNamespaceUrl);
        var data = document.CreateElement("ds", "X509Data", SignedXml.XmlDsigNamespaceUrl);
        AppendText(data, "ds", "X509Certificate", SignedXml.XmlDsigNamespaceUrl, Convert.ToBase64String(signingCertificate.RawData));
        keyInfo.AppendChild(data);
        key.AppendChild(keyInfo);
        idp.AppendChild(key);
        foreach (var binding in new[] { RedirectBinding, PostBinding })
            AppendService(idp, "SingleLogoutService", binding, singleLogoutUrl);
        foreach (var format in AuthCenter.Domain.Entities.SamlNameIdFormats.Supported)
            AppendText(idp, "md", "NameIDFormat", MetadataNamespace, format);
        foreach (var binding in new[] { RedirectBinding, PostBinding })
            AppendService(idp, "SingleSignOnService", binding, singleSignOnUrl);
        return document.OuterXml;
    }

    private static XmlElement BuildAssertion(XmlDocument document, string issuer, string recipient, string? inResponseTo, string audience, DateTime now, int lifetimeMinutes, SamlAssertionContent content)
    {
        var expires = Instant(now.AddMinutes(lifetimeMinutes));
        var assertion = document.CreateElement("saml", "Assertion", AssertionNamespace);
        assertion.SetAttribute("xmlns:xs", "http://www.w3.org/2001/XMLSchema");
        assertion.SetAttribute("xmlns:xsi", "http://www.w3.org/2001/XMLSchema-instance");
        assertion.SetAttribute("ID", NewId());
        assertion.SetAttribute("Version", "2.0");
        assertion.SetAttribute("IssueInstant", Instant(now));
        AppendText(assertion, "saml", "Issuer", AssertionNamespace, issuer);

        var subject = Append(assertion, "saml", "Subject", AssertionNamespace);
        var nameId = AppendText(subject, "saml", "NameID", AssertionNamespace, content.NameId);
        nameId.SetAttribute("Format", content.NameIdFormat);
        if (content.NameIdFormat == AuthCenter.Domain.Entities.SamlNameIdFormats.Persistent)
        {
            nameId.SetAttribute("NameQualifier", issuer);
            nameId.SetAttribute("SPNameQualifier", audience);
        }
        var confirmation = Append(subject, "saml", "SubjectConfirmation", AssertionNamespace);
        confirmation.SetAttribute("Method", "urn:oasis:names:tc:SAML:2.0:cm:bearer");
        var confirmationData = Append(confirmation, "saml", "SubjectConfirmationData", AssertionNamespace);
        if (inResponseTo is not null)
            confirmationData.SetAttribute("InResponseTo", inResponseTo);
        confirmationData.SetAttribute("NotOnOrAfter", expires);
        confirmationData.SetAttribute("Recipient", recipient);

        var conditions = Append(assertion, "saml", "Conditions", AssertionNamespace);
        // A minute of tolerance for the service provider's clock.
        conditions.SetAttribute("NotBefore", Instant(now.AddMinutes(-1)));
        conditions.SetAttribute("NotOnOrAfter", expires);
        var restriction = Append(conditions, "saml", "AudienceRestriction", AssertionNamespace);
        AppendText(restriction, "saml", "Audience", AssertionNamespace, audience);

        var authn = Append(assertion, "saml", "AuthnStatement", AssertionNamespace);
        authn.SetAttribute("AuthnInstant", Instant(content.AuthenticatedAt));
        authn.SetAttribute("SessionIndex", content.SessionIndex);
        var context = Append(authn, "saml", "AuthnContext", AssertionNamespace);
        AppendText(context, "saml", "AuthnContextClassRef", AssertionNamespace, content.AuthnContextClassRef);

        var attributes = content.Attributes.Where(attribute => attribute.Values.Count > 0).ToList();
        if (attributes.Count > 0)
        {
            var statement = Append(assertion, "saml", "AttributeStatement", AssertionNamespace);
            foreach (var attribute in attributes)
            {
                var element = Append(statement, "saml", "Attribute", AssertionNamespace);
                element.SetAttribute("Name", attribute.Name);
                element.SetAttribute("NameFormat", Uri.TryCreate(attribute.Name, UriKind.Absolute, out _)
                    ? "urn:oasis:names:tc:SAML:2.0:attrname-format:uri"
                    : "urn:oasis:names:tc:SAML:2.0:attrname-format:basic");
                foreach (var value in attribute.Values)
                {
                    var valueElement = AppendText(element, "saml", "AttributeValue", AssertionNamespace, value);
                    valueElement.SetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance", "xs:string");
                }
            }
        }
        return assertion;
    }

    private static void AppendStatus(XmlElement parent, (string Code, string? SubCode, string? Message)? failure)
    {
        var status = Append(parent, "samlp", "Status", ProtocolNamespace);
        var code = Append(status, "samlp", "StatusCode", ProtocolNamespace);
        code.SetAttribute("Value", failure?.Code ?? Success);
        if (failure?.SubCode is { } subCode)
            Append(code, "samlp", "StatusCode", ProtocolNamespace).SetAttribute("Value", subCode);
        if (failure?.Message is { } message)
            AppendText(status, "samlp", "StatusMessage", ProtocolNamespace, message);
    }

    /// <summary>An enveloped RSA-SHA256 signature with exclusive canonicalization, placed after the issuer.</summary>
    private static void Sign(XmlDocument document, XmlElement element, string id, X509Certificate2 certificate, XmlElement after)
    {
        using var key = certificate.GetRSAPrivateKey() ?? throw new CryptographicException("The signing certificate has no RSA private key.");
        var signedXml = new SignedXml(element) { SigningKey = key };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = RsaSha256;
        var reference = new Reference("#" + id) { DigestMethod = Sha256 };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificate));
        signedXml.KeyInfo = keyInfo;
        signedXml.ComputeSignature();
        element.InsertAfter(document.ImportNode(signedXml.GetXml(), true), after);
    }

    /// <summary>Replaces the (signed) assertion with its encryption: AES-256-CBC, the key wrapped with RSA-OAEP.</summary>
    private static void Encrypt(XmlDocument document, XmlElement assertion, X509Certificate2 certificate)
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        var encryptedData = new EncryptedData
        {
            Type = EncryptedXml.XmlEncElementUrl,
            EncryptionMethod = new EncryptionMethod(EncryptedXml.XmlEncAES256Url)
        };
        encryptedData.CipherData.CipherValue = new EncryptedXml().EncryptData(assertion, aes, false);
        using var publicKey = certificate.GetRSAPublicKey() ?? throw new CryptographicException("The encryption certificate has no RSA key.");
        var encryptedKey = new EncryptedKey
        {
            EncryptionMethod = new EncryptionMethod(EncryptedXml.XmlEncRSAOAEPUrl),
            CipherData = new CipherData(EncryptedXml.EncryptKey(aes.Key, publicKey, useOAEP: true))
        };
        encryptedKey.KeyInfo.AddClause(new KeyInfoX509Data(certificate));
        encryptedData.KeyInfo.AddClause(new KeyInfoEncryptedKey(encryptedKey));
        var wrapper = document.CreateElement("saml", "EncryptedAssertion", AssertionNamespace);
        wrapper.AppendChild(document.ImportNode(encryptedData.GetXml(), true));
        assertion.ParentNode!.ReplaceChild(wrapper, assertion);
    }

    private static void AppendService(XmlElement parent, string name, string binding, string location)
    {
        var service = Append(parent, "md", name, MetadataNamespace);
        service.SetAttribute("Binding", binding);
        service.SetAttribute("Location", location);
    }

    private static XmlElement Append(XmlElement parent, string prefix, string name, string ns)
    {
        var element = parent.OwnerDocument.CreateElement(prefix, name, ns);
        parent.AppendChild(element);
        return element;
    }

    private static XmlElement AppendText(XmlElement parent, string prefix, string name, string ns, string text)
    {
        var element = Append(parent, prefix, name, ns);
        element.InnerText = text;
        return element;
    }

    private static XmlElement? Child(XmlElement parent, string ns, string name) =>
        parent.ChildNodes.OfType<XmlElement>().FirstOrDefault(element => element.NamespaceURI == ns && element.LocalName == name);

    /// <summary>An AuthnRequest may name the user it expects (a login hint) in its Subject.</summary>
    private static string? SubjectNameId(XmlElement root) =>
        Child(root, AssertionNamespace, "Subject") is { } subject ? Child(subject, AssertionNamespace, "NameID")?.InnerText.Trim() : null;

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>An xs:ID: it must not start with a digit.</summary>
    private static string NewId() => "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    private static string Instant(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
