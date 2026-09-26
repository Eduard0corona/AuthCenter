using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Saml;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Saml;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services.Saml;

public sealed class SamlServiceProviderService : ISamlServiceProviderService
{
    private const int MaxAssertionConsumerServices = 10;
    private const int MaxAttributes = 30;
    private const string Invalid = "SAML_SP_INVALID";

    private readonly AuthCenterDbContext _db;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ISamlIdentityProviderService _identityProvider;

    public SamlServiceProviderService(AuthCenterDbContext db, IAuditService audit, IDateTimeProvider clock, ISamlIdentityProviderService identityProvider)
    {
        _db = db;
        _audit = audit;
        _clock = clock;
        _identityProvider = identityProvider;
    }

    public async Task<PagedResult<SamlServiceProviderDto>> GetAsync(SamlServiceProviderQuery query, CancellationToken ct = default)
    {
        var providers = _db.SamlServiceProviders.AsNoTracking().Include(item => item.ApplicationSystem).AsQueryable();
        if (query.ApplicationSystemId is { } applicationId)
            providers = providers.Where(item => item.ApplicationSystemId == applicationId);
        if (query.IsActive is { } active)
            providers = providers.Where(item => item.IsActive == active);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            providers = providers.Where(item => item.Name.Contains(search) || item.EntityId.Contains(search));
        }
        var total = await providers.CountAsync(ct);
        var items = await providers.OrderBy(item => item.Name).ThenBy(item => item.Id).Skip(query.Skip).Take(query.PageSize).ToListAsync(ct);
        return PagedResult<SamlServiceProviderDto>.Create(items.Select(Map).ToList(), total, query.Page, query.PageSize);
    }

    public async Task<SamlServiceProviderDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var provider = await _db.SamlServiceProviders.AsNoTracking().Include(item => item.ApplicationSystem).SingleOrDefaultAsync(item => item.Id == id, ct);
        return provider is null ? null : Map(provider);
    }

    public async Task<OperationResult<SamlServiceProviderDto>> CreateAsync(CreateSamlServiceProviderRequest request, CancellationToken ct = default)
    {
        if (!await _db.ApplicationSystems.AnyAsync(item => item.Id == request.ApplicationSystemId && item.IsActive, ct))
            return OperationResult<SamlServiceProviderDto>.Failure("APP_NOT_FOUND", "Active application not found.");
        var fields = await ValidateAsync(request, ct);
        if (!fields.IsSuccess)
            return OperationResult<SamlServiceProviderDto>.Failure(fields.ErrorCode, fields.Message);
        if (await _db.SamlServiceProviders.AnyAsync(item => item.EntityId == fields.Data!.EntityId, ct))
            return OperationResult<SamlServiceProviderDto>.Failure("SAML_SP_EXISTS", "Another SAML application uses this entity ID.");

        var provider = new SamlServiceProvider
        {
            Id = Guid.NewGuid(),
            ApplicationSystemId = request.ApplicationSystemId,
            NameIdSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            IsActive = true,
            CreatedAt = _clock.UtcNow
        };
        Apply(provider, fields.Data!);
        _db.SamlServiceProviders.Add(provider);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return OperationResult<SamlServiceProviderDto>.Failure("SAML_SP_EXISTS", "Another SAML application uses this entity ID.");
        }
        await _audit.LogAsync("SAML_SERVICE_PROVIDER_CREATED", entityName: nameof(SamlServiceProvider), entityId: provider.Id.ToString(), metadata: new { result = "Success", provider.EntityId, provider.ApplicationSystemId }, ct: ct);
        return OperationResult<SamlServiceProviderDto>.Success((await GetByIdAsync(provider.Id, ct))!);
    }

    public async Task<OperationResult<SamlServiceProviderDto>> UpdateAsync(Guid id, UpdateSamlServiceProviderRequest request, CancellationToken ct = default)
    {
        var provider = await _db.SamlServiceProviders.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (provider is null)
            return OperationResult<SamlServiceProviderDto>.Failure("SAML_SP_NOT_FOUND", "SAML application not found.");
        if (!provider.TryAdvance(request.Version))
            return OperationResult<SamlServiceProviderDto>.Failure(VersionedUpdates.ConflictCode, "The SAML application changed after it was loaded.");
        var fields = await ValidateAsync(request, ct);
        if (!fields.IsSuccess)
            return OperationResult<SamlServiceProviderDto>.Failure(fields.ErrorCode, fields.Message);
        if (await _db.SamlServiceProviders.AnyAsync(item => item.EntityId == fields.Data!.EntityId && item.Id != id, ct))
            return OperationResult<SamlServiceProviderDto>.Failure("SAML_SP_EXISTS", "Another SAML application uses this entity ID.");
        Apply(provider, fields.Data!);
        provider.IsActive = request.IsActive;
        provider.UpdatedAt = _clock.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return OperationResult<SamlServiceProviderDto>.Failure(VersionedUpdates.ConflictCode, "The SAML application changed after it was loaded.");
        }
        catch (DbUpdateException)
        {
            return OperationResult<SamlServiceProviderDto>.Failure("SAML_SP_EXISTS", "Another SAML application uses this entity ID.");
        }
        await _audit.LogAsync("SAML_SERVICE_PROVIDER_UPDATED", entityName: nameof(SamlServiceProvider), entityId: id.ToString(), metadata: new { result = "Success", provider.EntityId, provider.IsActive, provider.Version }, ct: ct);
        return OperationResult<SamlServiceProviderDto>.Success((await GetByIdAsync(id, ct))!);
    }

    public async Task<OperationResult> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var provider = await _db.SamlServiceProviders.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (provider is null)
            return OperationResult.Failure("SAML_SP_NOT_FOUND", "SAML application not found.");
        _db.SamlServiceProviders.Remove(provider);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("SAML_SERVICE_PROVIDER_DELETED", entityName: nameof(SamlServiceProvider), entityId: id.ToString(), metadata: new { result = "Success", provider.EntityId }, ct: ct);
        return OperationResult.Success();
    }

    /// <summary>
    /// Reads a service provider's metadata (an EntityDescriptor with an SPSSODescriptor): entity ID,
    /// HTTP-POST assertion consumer services (default first), single logout, certificates and name
    /// identifier format. Nothing is saved.
    /// </summary>
    public OperationResult<SamlServiceProviderMetadataDto> ParseMetadata(ParseSamlMetadataRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MetadataXml) || request.MetadataXml.Length > 512 * 1024)
            return OperationResult<SamlServiceProviderMetadataDto>.Failure("SAML_METADATA_INVALID", "Paste the service provider's metadata XML (at most 512 KB).");
        var document = new XmlDocument { XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(request.MetadataXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document.Load(reader);
        }
        catch (XmlException)
        {
            return OperationResult<SamlServiceProviderMetadataDto>.Failure("SAML_METADATA_INVALID", "The metadata is not well-formed XML.");
        }
        const string metadata = SamlIdpMessages.MetadataNamespace;
        var descriptor = document.GetElementsByTagName("SPSSODescriptor", metadata).OfType<XmlElement>().FirstOrDefault();
        if (descriptor?.ParentNode is not XmlElement entity || entity.LocalName != "EntityDescriptor" || string.IsNullOrWhiteSpace(entity.GetAttribute("entityID")))
            return OperationResult<SamlServiceProviderMetadataDto>.Failure("SAML_METADATA_INVALID", "The metadata has no service provider descriptor (SPSSODescriptor) with an entityID.");

        var warnings = new List<string>();
        var services = descriptor.ChildNodes.OfType<XmlElement>().Where(element => element.NamespaceURI == metadata && element.LocalName == "AssertionConsumerService").ToList();
        var post = services.Where(service => service.GetAttribute("Binding") == SamlIdpMessages.PostBinding)
            .OrderByDescending(service => service.GetAttribute("isDefault") == "true")
            .ThenBy(service => int.TryParse(service.GetAttribute("index"), out var index) ? index : int.MaxValue)
            .Select(service => service.GetAttribute("Location").Trim()).Where(location => location.Length > 0).Distinct().ToList();
        if (services.Count > post.Count)
            warnings.Add("Only HTTP-POST assertion consumer services are used; the others were left out.");
        var logout = descriptor.ChildNodes.OfType<XmlElement>().Where(element => element.NamespaceURI == metadata && element.LocalName == "SingleLogoutService").ToList();
        var logoutUrl = (logout.FirstOrDefault(service => service.GetAttribute("Binding") == SamlIdpMessages.PostBinding) ?? logout.FirstOrDefault())?.GetAttribute("Location").Trim();
        var formats = descriptor.ChildNodes.OfType<XmlElement>().Where(element => element.NamespaceURI == metadata && element.LocalName == "NameIDFormat").Select(element => element.InnerText.Trim()).ToList();
        var format = formats.FirstOrDefault(SamlNameIdFormats.Supported.Contains);
        if (formats.Count > 0 && format is null)
            warnings.Add("None of the name identifier formats in the metadata is supported; choose one.");

        string? Certificate(string use) => descriptor.ChildNodes.OfType<XmlElement>()
            .Where(element => element.NamespaceURI == metadata && element.LocalName == "KeyDescriptor" && (element.GetAttribute("use") is var value && (value == use || value.Length == 0)))
            .SelectMany(element => element.GetElementsByTagName("X509Certificate", "http://www.w3.org/2000/09/xmldsig#").OfType<XmlElement>())
            .Select(element => string.Concat(element.InnerText.Where(character => !char.IsWhiteSpace(character))))
            .FirstOrDefault(value => value.Length > 0);

        return OperationResult<SamlServiceProviderMetadataDto>.Success(new SamlServiceProviderMetadataDto
        {
            EntityId = entity.GetAttribute("entityID").Trim(),
            AssertionConsumerServiceUrls = post,
            SingleLogoutServiceUrl = string.IsNullOrEmpty(logoutUrl) ? null : logoutUrl,
            NameIdFormat = format,
            SigningCertificate = Certificate("signing") is { } signing ? ToPem(signing) : null,
            EncryptionCertificate = Certificate("encryption") is { } encryption ? ToPem(encryption) : null,
            RequireSignedRequests = descriptor.GetAttribute("AuthnRequestsSigned") == "true",
            Warnings = warnings
        });
    }

    public SamlIdentityProviderDto DescribeIdentityProvider()
    {
        var info = _identityProvider.Describe();
        return new SamlIdentityProviderDto
        {
            IsConfigured = info.IsConfigured,
            Problem = info.Problem,
            EntityId = info.EntityId,
            MetadataUrl = info.MetadataUrl,
            SingleSignOnUrl = info.SingleSignOnUrl,
            SingleLogoutUrl = info.SingleLogoutUrl,
            Certificate = info.CertificatePem is null ? null : CertificateDto(Convert.ToBase64String(X509Certificate2.CreateFromPem(info.CertificatePem).RawData)),
            NameIdFormats = SamlNameIdFormats.Supported,
            AttributeSources = [.. SamlAttributeMapping.Sources, $"{SamlAttributeMapping.ProfilePrefix}<clave>"]
        };
    }

    private async Task<OperationResult<ValidFields>> ValidateAsync(SamlServiceProviderFields request, CancellationToken ct)
    {
        OperationResult<ValidFields> Fail(string message) => OperationResult<ValidFields>.Failure(Invalid, message);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 150)
            return Fail("Name is required, at most 150 characters.");
        var entityId = request.EntityId?.Trim() ?? string.Empty;
        if (entityId.Length is 0 or > 500 || entityId.Any(char.IsWhiteSpace))
            return Fail("The entity ID is required: at most 500 characters, without spaces.");
        var services = request.AssertionConsumerServiceUrls.Select(url => url?.Trim() ?? string.Empty).Where(url => url.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (services.Count is 0 or > MaxAssertionConsumerServices)
            return Fail($"Register 1 to {MaxAssertionConsumerServices} assertion consumer service URLs.");
        if (services.FirstOrDefault(url => !IsBrowserEndpoint(url)) is { } badService)
            return Fail($"{badService} is not an HTTPS URL (HTTP is only accepted for localhost).");
        var logout = string.IsNullOrWhiteSpace(request.SingleLogoutServiceUrl) ? null : request.SingleLogoutServiceUrl.Trim();
        if (logout is not null && !IsBrowserEndpoint(logout))
            return Fail("The single logout URL must be HTTPS (HTTP is only accepted for localhost).");
        if (!SamlNameIdFormats.Supported.Contains(request.NameIdFormat))
            return Fail("Choose a supported name identifier format.");
        var signing = ReadCertificate(request.SigningCertificate, out var signingError);
        if (signingError is not null)
            return Fail($"Signing certificate: {signingError}");
        if (request.RequireSignedRequests && signing is null)
            return Fail("Requiring signed requests needs the service provider's signing certificate.");
        var encryption = ReadCertificate(request.EncryptionCertificate, out var encryptionError);
        if (encryptionError is not null)
            return Fail($"Encryption certificate: {encryptionError}");
        if (request.EncryptAssertions && encryption is null)
            return Fail("Encrypting assertions needs the service provider's encryption certificate.");
        if (request.AssertionLifetimeMinutes is < 1 or > 60)
            return Fail("The assertion lifetime is 1 to 60 minutes.");
        var relayState = string.IsNullOrWhiteSpace(request.DefaultRelayState) ? null : request.DefaultRelayState.Trim();
        if (relayState?.Length > 500)
            return Fail("The default RelayState is at most 500 characters.");

        if (request.Attributes.Count > MaxAttributes)
            return Fail($"At most {MaxAttributes} attributes.");
        var profileKeys = await _db.UserProfileAttributeDefinitions.AsNoTracking().Where(item => item.IsActive).Select(item => item.Key).ToListAsync(ct);
        var attributes = new List<SamlAttributeMapping>();
        foreach (var attribute in request.Attributes)
        {
            var attributeName = attribute.Name?.Trim() ?? string.Empty;
            if (attributeName.Length is 0 or > 200 || attributeName.Any(char.IsControl))
                return Fail("Each attribute needs a name of at most 200 characters.");
            if (attributes.Any(existing => string.Equals(existing.Name, attributeName, StringComparison.OrdinalIgnoreCase)))
                return Fail($"The attribute {attributeName} is repeated.");
            var source = attribute.Source?.Trim() ?? string.Empty;
            var valid = SamlAttributeMapping.Sources.Contains(source) ||
                source.StartsWith(SamlAttributeMapping.ProfilePrefix, StringComparison.Ordinal) &&
                profileKeys.Contains(source[SamlAttributeMapping.ProfilePrefix.Length..], StringComparer.OrdinalIgnoreCase);
            if (!valid)
                return Fail($"The source of {attributeName} must be {string.Join(", ", SamlAttributeMapping.Sources)} or profile: and an active profile attribute.");
            attributes.Add(new SamlAttributeMapping(attributeName, source));
        }
        return OperationResult<ValidFields>.Success(new ValidFields(name, entityId, services, logout, request.NameIdFormat, signing, request.RequireSignedRequests,
            encryption, request.EncryptAssertions, request.SignResponse, attributes, request.AllowIdpInitiated, relayState, request.AssertionLifetimeMinutes));
    }

    private static void Apply(SamlServiceProvider provider, ValidFields fields)
    {
        provider.Name = fields.Name;
        provider.EntityId = fields.EntityId;
        provider.AssertionConsumerServiceUrlsJson = JsonSerializer.Serialize(fields.AssertionConsumerServiceUrls);
        provider.SingleLogoutServiceUrl = fields.SingleLogoutServiceUrl;
        provider.NameIdFormat = fields.NameIdFormat;
        provider.SigningCertificate = fields.SigningCertificate;
        provider.RequireSignedRequests = fields.RequireSignedRequests;
        provider.EncryptionCertificate = fields.EncryptionCertificate;
        provider.EncryptAssertions = fields.EncryptAssertions;
        provider.SignResponse = fields.SignResponse;
        provider.AttributesJson = JsonSerializer.Serialize(fields.Attributes, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        provider.AllowIdpInitiated = fields.AllowIdpInitiated;
        provider.DefaultRelayState = fields.DefaultRelayState;
        provider.AssertionLifetimeMinutes = fields.AssertionLifetimeMinutes;
    }

    private static SamlServiceProviderDto Map(SamlServiceProvider provider) => new()
    {
        Version = provider.Version,
        Id = provider.Id,
        ApplicationSystemId = provider.ApplicationSystemId,
        ApplicationCode = provider.ApplicationSystem.Code,
        ApplicationName = provider.ApplicationSystem.Name,
        Name = provider.Name,
        EntityId = provider.EntityId,
        AssertionConsumerServiceUrls = SamlIdentityProviderService.AssertionConsumerServices(provider),
        SingleLogoutServiceUrl = provider.SingleLogoutServiceUrl,
        NameIdFormat = provider.NameIdFormat,
        SigningCertificate = provider.SigningCertificate is null ? null : CertificateDto(provider.SigningCertificate),
        RequireSignedRequests = provider.RequireSignedRequests,
        EncryptionCertificate = provider.EncryptionCertificate is null ? null : CertificateDto(provider.EncryptionCertificate),
        EncryptAssertions = provider.EncryptAssertions,
        SignResponse = provider.SignResponse,
        Attributes = SamlAttributeMapping.Read(provider.AttributesJson).Select(item => new SamlAttributeMappingDto { Name = item.Name, Source = item.Source }).ToList(),
        AllowIdpInitiated = provider.AllowIdpInitiated,
        DefaultRelayState = provider.DefaultRelayState,
        LaunchUrl = provider.AllowIdpInitiated ? $"/saml/idp/sso/initiate/{provider.Id}" : null,
        AssertionLifetimeMinutes = provider.AssertionLifetimeMinutes,
        IsActive = provider.IsActive,
        CreatedAt = provider.CreatedAt,
        UpdatedAt = provider.UpdatedAt
    };

    private static SamlCertificateDto? CertificateDto(string base64)
    {
        var certificate = SamlIdentityProviderService.Certificate(base64);
        return certificate is null ? null : new SamlCertificateDto
        {
            Pem = certificate.ExportCertificatePem(),
            Subject = certificate.Subject,
            ThumbprintSha256 = Convert.ToHexString(SHA256.HashData(certificate.RawData)),
            NotBefore = certificate.NotBefore.ToUniversalTime(),
            NotAfter = certificate.NotAfter.ToUniversalTime()
        };
    }

    /// <summary>A certificate as PEM or base64 DER, stored as base64 DER; RSA of at least 2048 bits.</summary>
    private static string? ReadCertificate(string? value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            var text = value.Trim();
            using var certificate = text.Contains("-----BEGIN", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(text)
                : X509CertificateLoader.LoadCertificate(Convert.FromBase64String(string.Concat(text.Where(character => !char.IsWhiteSpace(character)))));
            using var key = certificate.GetRSAPublicKey();
            if (key is null || key.KeySize < 2048)
            {
                error = "it needs an RSA key of at least 2048 bits.";
                return null;
            }
            return Convert.ToBase64String(certificate.RawData);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            error = "it is not a valid X.509 certificate (PEM or base64).";
            return null;
        }
    }

    private static string ToPem(string base64) =>
        SamlIdentityProviderService.Certificate(base64)?.ExportCertificatePem() ?? base64;

    /// <summary>Assertions are posted by the browser, never by AuthCenter: HTTPS, or HTTP on this computer for development.</summary>
    private static bool IsBrowserEndpoint(string value) =>
        value.Length <= 1000 && Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.Fragment) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    private sealed record ValidFields(
        string Name, string EntityId, IReadOnlyList<string> AssertionConsumerServiceUrls, string? SingleLogoutServiceUrl, string NameIdFormat,
        string? SigningCertificate, bool RequireSignedRequests, string? EncryptionCertificate, bool EncryptAssertions, bool SignResponse,
        IReadOnlyList<SamlAttributeMapping> Attributes, bool AllowIdpInitiated, string? DefaultRelayState, int AssertionLifetimeMinutes);
}
