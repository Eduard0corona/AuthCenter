using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Federation;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Federation;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services;

public sealed partial class FederationService : IFederationService
{
    private const string OidcStatePurpose = "federation_oidc";
    private readonly AuthCenterDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IUserAccessService _access;
    private readonly IAuthenticationSessionIssuer _sessions;
    private readonly ITransientStateStore _state;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;
    private readonly IHttpClientFactory _httpClients;
    private readonly IDataProtector _secrets;
    private readonly SamlSettings _samlSettings;
    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> OidcConfigurations = new(StringComparer.Ordinal);

    public FederationService(
        AuthCenterDbContext db,
        UserManager<ApplicationUser> users,
        IUserAccessService access,
        IAuthenticationSessionIssuer sessions,
        ITransientStateStore state,
        IAuditService audit,
        IDateTimeProvider clock,
        IHttpClientFactory httpClients,
        IDataProtectionProvider dataProtection,
        IOptions<SamlSettings> samlSettings)
    {
        _db = db;
        _users = users;
        _access = access;
        _sessions = sessions;
        _state = state;
        _audit = audit;
        _clock = clock;
        _httpClients = httpClients;
        _secrets = dataProtection.CreateProtector("AuthCenter.FederationProviderSecrets.v1");
        _samlSettings = samlSettings.Value;
    }

    public async Task<IReadOnlyList<FederationProviderDto>> GetProvidersAsync(Guid? applicationSystemId, CancellationToken ct = default) =>
        (await _db.FederationProviders.AsNoTracking()
            .Where(item => !applicationSystemId.HasValue || item.ApplicationSystemId == applicationSystemId)
            .OrderBy(item => item.Name).ToListAsync(ct)).Select(Map).ToList();

    public Task<OperationResult<FederationProviderDto>> CreateProviderAsync(UpsertFederationProviderRequest request, CancellationToken ct = default) =>
        SaveProviderAsync(null, request, ct);

    public Task<OperationResult<FederationProviderDto>> UpdateProviderAsync(Guid id, UpsertFederationProviderRequest request, CancellationToken ct = default) =>
        SaveProviderAsync(id, request, ct);

    public async Task<OperationResult> DeleteProviderAsync(Guid id, CancellationToken ct = default)
    {
        var provider = await _db.FederationProviders.FindAsync([id], ct);
        if (provider is null) return OperationResult.Failure("FEDERATION_PROVIDER_NOT_FOUND", "Federation provider not found.");
        _db.FederationProviders.Remove(provider);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("FEDERATION_PROVIDER_DELETED", entityName: nameof(FederationProvider), entityId: id.ToString(), ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> CreateRoutingRuleAsync(CreateFederationRoutingRuleRequest request, CancellationToken ct = default)
    {
        if (request.Priority is < 1 or > 10000 ||
            (string.IsNullOrWhiteSpace(request.EmailDomain) && !request.DirectoryGroupId.HasValue && !request.ProfileAttributeDefinitionId.HasValue))
            return OperationResult.Failure("INVALID_ROUTING_RULE", "Priority and at least one domain, group, or profile condition are required.");
        if (!await _db.FederationProviders.AnyAsync(item => item.Id == request.FederationProviderId, ct))
            return OperationResult.Failure("FEDERATION_PROVIDER_NOT_FOUND", "Federation provider not found.");
        if (await _db.FederationRoutingRules.AnyAsync(item => item.FederationProviderId == request.FederationProviderId && item.Priority == request.Priority, ct))
            return OperationResult.Failure("ROUTING_PRIORITY_EXISTS", "The provider already has a rule with this priority.");

        var rule = new FederationRoutingRule
        {
            Id = Guid.NewGuid(),
            FederationProviderId = request.FederationProviderId,
            Priority = request.Priority,
            EmailDomain = NormalizeDomain(request.EmailDomain),
            DirectoryGroupId = request.DirectoryGroupId,
            ProfileAttributeDefinitionId = request.ProfileAttributeDefinitionId,
            ExpectedProfileValueJson = request.ExpectedProfileValueJson,
            IsActive = request.IsActive,
            CreatedAt = _clock.UtcNow
        };
        _db.FederationRoutingRules.Add(rule);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("FEDERATION_ROUTING_RULE_CREATED", entityName: nameof(FederationRoutingRule), entityId: rule.Id.ToString(), ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult<FederationRouteResponse>> RouteAsync(FederationRouteRequest request, CancellationToken ct = default)
    {
        var normalizedEmail = request.Email.Trim().ToUpperInvariant();
        var domain = NormalizeDomain(request.Email.Split('@').LastOrDefault());
        var user = await _db.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(item => item.NormalizedEmail == normalizedEmail && item.DeletedAt == null, ct);
        var candidates = await _db.FederationRoutingRules.AsNoTracking()
            .Include(item => item.FederationProvider).ThenInclude(item => item.ApplicationSystem)
            .Where(item => item.IsActive && item.FederationProvider.IsActive && item.FederationProvider.ApplicationSystem.IsActive &&
                item.FederationProvider.ApplicationSystem.Code == request.ApplicationCode)
            .OrderBy(item => item.Priority).ToListAsync(ct);

        foreach (var rule in candidates)
        {
            if (rule.EmailDomain is not null && !string.Equals(rule.EmailDomain, domain, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.DirectoryGroupId.HasValue && (user is null || !await _db.UserGroupMemberships.AnyAsync(item => item.UserId == user.Id && item.GroupId == rule.DirectoryGroupId, ct))) continue;
            if (rule.ProfileAttributeDefinitionId.HasValue && (user is null || !await _db.UserProfileAttributeValues.AnyAsync(item => item.UserId == user.Id && item.AttributeDefinitionId == rule.ProfileAttributeDefinitionId && item.ValueJson == rule.ExpectedProfileValueJson, ct))) continue;
            return OperationResult<FederationRouteResponse>.Success(new FederationRouteResponse
            {
                ProviderId = rule.FederationProviderId,
                ProviderName = rule.FederationProvider.Name,
                Protocol = rule.FederationProvider.Protocol.ToString()
            });
        }
        return OperationResult<FederationRouteResponse>.Failure("FEDERATION_ROUTE_NOT_FOUND", "No active federation route matched this application and identity.");
    }

    public async Task<OperationResult<OidcFederationChallengeResponse>> BeginOidcAsync(BeginOidcFederationRequest request, CancellationToken ct = default)
    {
        var provider = await _db.FederationProviders.AsNoTracking().FirstOrDefaultAsync(item => item.Id == request.ProviderId && item.IsActive && item.Protocol == FederationProtocol.Oidc, ct);
        if (provider is null) return OperationResult<OidcFederationChallengeResponse>.Failure("FEDERATION_PROVIDER_NOT_FOUND", "Active OIDC provider not found.");
        var configuration = await GetOidcConfigurationAsync(provider, ct);
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.AuthorizationEndpoint))
            return OperationResult<OidcFederationChallengeResponse>.Failure("OIDC_DISCOVERY_FAILED", "OIDC discovery document is unavailable or incomplete.");

        var interactionId = Guid.NewGuid().ToString("N");
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        await _state.SetAsync(OidcStatePurpose, interactionId, JsonSerializer.Serialize(new OidcInteraction(provider.Id, state, nonce, verifier)), _clock.UtcNow.AddMinutes(5), ct);

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = provider.ClientId,
            ["redirect_uri"] = provider.OidcCallbackUrl,
            ["response_type"] = "code",
            ["scope"] = "openid profile email",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["login_hint"] = string.IsNullOrWhiteSpace(request.LoginHint) ? null : request.LoginHint.Trim()
        };
        return OperationResult<OidcFederationChallengeResponse>.Success(new OidcFederationChallengeResponse
        {
            InteractionId = interactionId,
            AuthorizationUrl = QueryHelpers.AddQueryString(configuration.AuthorizationEndpoint, query),
            ExpiresIn = 300
        });
    }

    public async Task<OperationResult<AuthResponse>> CompleteOidcAsync(CompleteOidcFederationRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var raw = await _state.TakeAsync(OidcStatePurpose, request.InteractionId, ct);
        var interaction = string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<OidcInteraction>(raw);
        if (interaction is null || !FixedEquals(interaction.State, request.State) || string.IsNullOrWhiteSpace(request.Code))
            return OperationResult<AuthResponse>.Failure("INVALID_OIDC_STATE", "OIDC interaction is invalid, expired, or already used.");
        var provider = await _db.FederationProviders.Include(item => item.ApplicationSystem).FirstOrDefaultAsync(item => item.Id == interaction.ProviderId && item.IsActive, ct);
        if (provider is null) return OperationResult<AuthResponse>.Failure("FEDERATION_PROVIDER_NOT_FOUND", "Federation provider not found.");
        var configuration = await GetOidcConfigurationAsync(provider, ct);
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.TokenEndpoint)) return OperationResult<AuthResponse>.Failure("OIDC_DISCOVERY_FAILED", "OIDC token endpoint is unavailable.");

        var tokenResponse = await RedeemCodeAsync(provider, configuration.TokenEndpoint, request.Code, interaction.Verifier, ct);
        if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.IdToken)) return OperationResult<AuthResponse>.Failure("OIDC_CODE_REJECTED", "The upstream provider rejected the authorization code.");
        var principal = ValidateIdToken(tokenResponse.IdToken, provider, configuration, interaction.Nonce);
        if (principal is null)
        {
            configuration = await GetOidcConfigurationAsync(provider, ct, refresh: true);
            principal = configuration is null ? null : ValidateIdToken(tokenResponse.IdToken, provider, configuration, interaction.Nonce);
        }
        if (principal is null) return OperationResult<AuthResponse>.Failure("INVALID_OIDC_TOKEN", "The upstream ID token failed signature, issuer, audience, lifetime, or nonce validation.");

        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = principal.FindFirstValue(JwtRegisteredClaimNames.Email) ?? principal.FindFirstValue(ClaimTypes.Email);
        var emailVerified = string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email) || !emailVerified)
            return OperationResult<AuthResponse>.Failure("OIDC_CLAIMS_INCOMPLETE", "A stable subject and verified email are required.");

        var userResult = await ResolveFederatedUserAsync(provider, subject, email, principal.FindFirstValue("name") ?? email, ct);
        if (!userResult.IsSuccess) return OperationResult<AuthResponse>.Failure(userResult.ErrorCode!, userResult.Message!);
        var user = userResult.Data!;
        await _audit.LogAsync("FEDERATION_LOGIN_SUCCESS", user.Id, provider.ApplicationSystem.Code, nameof(FederationProvider), provider.Id.ToString(), ipAddress, userAgent, new { protocol = "OIDC" }, ct);
        return await _sessions.IssueAsync(user, provider.ApplicationSystemId, provider.ApplicationSystem.Code, ipAddress, userAgent, ct: ct);
    }

    private async Task<OperationResult<ApplicationUser>> ResolveFederatedUserAsync(FederationProvider provider, string subject, string email, string name, CancellationToken ct)
    {
        var providerKey = $"Federation:{provider.Id:N}";
        var link = await _db.ExternalIdentityProviders.Include(item => item.User)
            .FirstOrDefaultAsync(item => item.Provider == providerKey && item.ProviderUserId == subject && item.IsActive, ct);
        ApplicationUser? user = link?.User;
        if (user is null)
        {
            user = await _users.FindByEmailAsync(email);
            if (user is not null && provider.AccountLinkingMode != AccountLinkingMode.VerifiedEmail)
                return OperationResult<ApplicationUser>.Failure("ACCOUNT_LINKING_REQUIRED", "An existing account requires an explicit linking policy.");
            if (user is null)
            {
                if (!provider.JitProvisioningEnabled) return OperationResult<ApplicationUser>.Failure("JIT_PROVISIONING_DISABLED", "No linked account exists and JIT provisioning is disabled.");
                user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email, FullName = name, EmailConfirmed = true, IsExternalUser = true, HasLocalPassword = false, IsActive = true, CreatedAt = _clock.UtcNow };
                var created = await _users.CreateAsync(user);
                if (!created.Succeeded) return OperationResult<ApplicationUser>.Failure("JIT_PROVISIONING_FAILED", string.Join("; ", created.Errors.Select(item => item.Description)));
                await _audit.LogAsync("FEDERATION_USER_JIT_PROVISIONED", user.Id, provider.ApplicationSystem.Code, nameof(FederationProvider), provider.Id.ToString(), ct: ct);
            }
            _db.ExternalIdentityProviders.Add(new ExternalIdentityProvider { Id = Guid.NewGuid(), UserId = user.Id, Provider = providerKey, ProviderUserId = subject, Email = email, DisplayName = name, LinkedAt = _clock.UtcNow, LastUsedAt = _clock.UtcNow, IsActive = true });
        }
        else link!.LastUsedAt = _clock.UtcNow;

        if (!user.IsActive || user.DeletedAt is not null) return OperationResult<ApplicationUser>.Failure("USER_INACTIVE", "Linked user is inactive.");
        if (!await _access.HasActiveAccessAsync(user.Id, provider.ApplicationSystemId, ct)) await _access.GrantAccessAsync(user.Id, provider.ApplicationSystemId, true, ct);
        await _db.SaveChangesAsync(ct);
        return OperationResult<ApplicationUser>.Success(user);
    }

    private async Task<OperationResult<FederationProviderDto>> SaveProviderAsync(Guid? id, UpsertFederationProviderRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<FederationProtocol>(request.Protocol, true, out var protocol) || !Enum.IsDefined(protocol) ||
            !Enum.TryParse<AccountLinkingMode>(request.AccountLinkingMode, true, out var linking) || !Enum.IsDefined(linking) ||
            string.IsNullOrWhiteSpace(request.Name) || !Uri.TryCreate(request.Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps)
            return OperationResult<FederationProviderDto>.Failure("INVALID_FEDERATION_PROVIDER", "Name, HTTPS issuer, protocol, and linking mode are required.");
        if (!await _db.ApplicationSystems.AnyAsync(item => item.Id == request.ApplicationSystemId && item.IsActive, ct))
            return OperationResult<FederationProviderDto>.Failure("APP_NOT_FOUND", "Active application not found.");
        if (protocol == FederationProtocol.Oidc && (string.IsNullOrWhiteSpace(request.ClientId) || !IsHttps(request.OidcCallbackUrl)))
            return OperationResult<FederationProviderDto>.Failure("INVALID_OIDC_PROVIDER", "OIDC client ID and exact HTTPS callback URL are required.");
        if (protocol == FederationProtocol.Saml2 && (!IsHttps(request.SamlSingleSignOnUrl) || !TryCertificate(request.SamlSigningCertificatePem, out _)))
            return OperationResult<FederationProviderDto>.Failure("INVALID_SAML_PROVIDER", "SAML HTTPS SSO URL and a valid signing certificate are required.");

        var provider = id.HasValue ? await _db.FederationProviders.FindAsync([id.Value], ct) : null;
        if (id.HasValue && provider is null) return OperationResult<FederationProviderDto>.Failure("FEDERATION_PROVIDER_NOT_FOUND", "Federation provider not found.");
        provider ??= new FederationProvider { Id = Guid.NewGuid(), CreatedAt = _clock.UtcNow };
        provider.ApplicationSystemId = request.ApplicationSystemId; provider.Name = request.Name.Trim(); provider.Protocol = protocol; provider.Issuer = issuer.AbsoluteUri.TrimEnd('/');
        provider.DiscoveryEndpoint = string.IsNullOrWhiteSpace(request.DiscoveryEndpoint) ? null : request.DiscoveryEndpoint.Trim(); provider.ClientId = request.ClientId?.Trim(); provider.OidcCallbackUrl = request.OidcCallbackUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(request.ClientSecret)) provider.ProtectedClientSecret = _secrets.Protect(request.ClientSecret);
        provider.SamlSingleSignOnUrl = request.SamlSingleSignOnUrl?.Trim(); provider.SamlSigningCertificatePem = request.SamlSigningCertificatePem?.Trim();
        provider.JitProvisioningEnabled = request.JitProvisioningEnabled; provider.AccountLinkingMode = linking; provider.IsActive = request.IsActive; provider.UpdatedAt = id.HasValue ? _clock.UtcNow : null;
        if (!id.HasValue) _db.FederationProviders.Add(provider);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return OperationResult<FederationProviderDto>.Failure("FEDERATION_PROVIDER_EXISTS", "A provider with this name already exists for the application."); }
        await _audit.LogAsync(id.HasValue ? "FEDERATION_PROVIDER_UPDATED" : "FEDERATION_PROVIDER_CREATED", entityName: nameof(FederationProvider), entityId: provider.Id.ToString(), ct: ct);
        return OperationResult<FederationProviderDto>.Success(Map(provider));
    }

    private async Task<OpenIdConnectConfiguration?> GetOidcConfigurationAsync(FederationProvider provider, CancellationToken ct, bool refresh = false)
    {
        var address = provider.DiscoveryEndpoint ?? $"{provider.Issuer.TrimEnd('/')}/.well-known/openid-configuration";
        var manager = OidcConfigurations.GetOrAdd($"{provider.Id:N}|{address}", _ => new ConfigurationManager<OpenIdConnectConfiguration>(address, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true }));
        if (refresh) manager.RequestRefresh();
        try { return await manager.GetConfigurationAsync(ct); } catch (Exception exception) when (exception is IOException or InvalidOperationException) { return null; }
    }

    private async Task<OidcTokenResponse?> RedeemCodeAsync(FederationProvider provider, string endpoint, string code, string verifier, CancellationToken ct)
    {
        var values = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["client_id"] = provider.ClientId!, ["code"] = code, ["redirect_uri"] = provider.OidcCallbackUrl!, ["code_verifier"] = verifier };
        if (!string.IsNullOrWhiteSpace(provider.ProtectedClientSecret)) values["client_secret"] = _secrets.Unprotect(provider.ProtectedClientSecret);
        using var response = await _httpClients.CreateClient("Federation").PostAsync(endpoint, new FormUrlEncodedContent(values), ct);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return new OidcTokenResponse(document.RootElement.TryGetProperty("id_token", out var token) ? token.GetString() : null);
    }

    private static ClaimsPrincipal? ValidateIdToken(string token, FederationProvider provider, OpenIdConnectConfiguration configuration, string nonce)
    {
        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = provider.Issuer,
                ValidateAudience = true,
                ValidAudience = provider.ClientId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = configuration.SigningKeys,
                ClockSkew = TimeSpan.FromMinutes(2),
                NameClaimType = "name"
            }, out var validated);
            if (validated is not JwtSecurityToken jwt || !FixedEquals(jwt.Claims.FirstOrDefault(item => item.Type == "nonce")?.Value, nonce)) return null;
            return principal;
        }
        catch (SecurityTokenException) { return null; }
    }

    private static FederationProviderDto Map(FederationProvider item) => new()
    {
        Id = item.Id,
        ApplicationSystemId = item.ApplicationSystemId,
        Name = item.Name,
        Protocol = item.Protocol.ToString(),
        Issuer = item.Issuer,
        DiscoveryEndpoint = item.DiscoveryEndpoint,
        ClientId = item.ClientId,
        OidcCallbackUrl = item.OidcCallbackUrl,
        HasClientSecret = !string.IsNullOrWhiteSpace(item.ProtectedClientSecret),
        SamlSingleSignOnUrl = item.SamlSingleSignOnUrl,
        SamlSigningCertificateThumbprint = TryCertificate(item.SamlSigningCertificatePem, out var certificate) ? certificate!.Thumbprint : null,
        JitProvisioningEnabled = item.JitProvisioningEnabled,
        AccountLinkingMode = item.AccountLinkingMode.ToString(),
        IsActive = item.IsActive
    };
    private static bool IsHttps(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.Fragment);
    private static string? NormalizeDomain(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('@').ToLowerInvariant();
    private static bool FixedEquals(string? left, string? right) => left is not null && right is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private static bool TryCertificate(string? pem, out X509Certificate2? certificate) { certificate = null; try { if (string.IsNullOrWhiteSpace(pem)) return false; certificate = X509Certificate2.CreateFromPem(pem); return true; } catch (CryptographicException) { return false; } }
    private sealed record OidcInteraction(Guid ProviderId, string State, string Nonce, string Verifier);
    private sealed record OidcTokenResponse(string? IdToken);
}
