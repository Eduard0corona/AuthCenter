using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Federation;
using AuthCenter.Contracts.Responses.Federation;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services;

/// <summary>
/// Enterprise federation: provider and routing administration, home realm discovery, the upstream
/// OIDC and SAML protocols and the mapping of upstream identities to AuthCenter users.
/// </summary>
public sealed partial class FederationService : IFederationService
{
    /// <summary>Path of the server-side OIDC callback the hosted login uses.</summary>
    public const string HostedOidcCallbackPath = "/api/federation/oidc/callback";
    private const int MaximumGroupMappings = 200;

    private readonly AuthCenterDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IUserAccessService _access;
    private readonly IAuthService _auth;
    private readonly IOAuthAuthorizationService _oauth;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly ITransientStateStore _state;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;
    private readonly IHttpClientFactory _httpClients;
    private readonly FederationMetadataCache _metadata;
    private readonly IDataProtector _secrets;
    private readonly SamlSettings _samlSettings;
    private readonly OidcSettings _oidcSettings;
    private readonly IEmailService _email;

    public FederationService(
        AuthCenterDbContext db,
        UserManager<ApplicationUser> users,
        IUserAccessService access,
        IAuthService auth,
        IOAuthAuthorizationService oauth,
        IRefreshTokenService refreshTokens,
        ITransientStateStore state,
        IAuditService audit,
        IDateTimeProvider clock,
        IHttpClientFactory httpClients,
        FederationMetadataCache metadata,
        IDataProtectionProvider dataProtection,
        IOptions<SamlSettings> samlSettings,
        IOptions<OidcSettings> oidcSettings,
        IEmailService email)
    {
        _db = db;
        _users = users;
        _access = access;
        _auth = auth;
        _oauth = oauth;
        _refreshTokens = refreshTokens;
        _state = state;
        _audit = audit;
        _clock = clock;
        _httpClients = httpClients;
        _metadata = metadata;
        _secrets = dataProtection.CreateProtector("AuthCenter.FederationProviderSecrets.v1");
        _samlSettings = samlSettings.Value;
        _oidcSettings = oidcSettings.Value;
        _email = email;
    }

    /// <summary>The absolute hosted OIDC callback, when <c>Oidc:PublicOrigin</c> is configured.</summary>
    private string? HostedOidcCallbackUrl =>
        _oidcSettings.NormalizedPublicOrigin is { } origin ? origin + HostedOidcCallbackPath : null;

    public FederationServiceProviderResponse GetServiceProviderInfo() => new()
    {
        OidcCallbackUrl = HostedOidcCallbackUrl,
        SamlEntityId = string.IsNullOrWhiteSpace(_samlSettings.EntityId) ? null : _samlSettings.EntityId,
        SamlAssertionConsumerServiceUrl = string.IsNullOrWhiteSpace(_samlSettings.AssertionConsumerServiceUrl) ? null : _samlSettings.AssertionConsumerServiceUrl
    };

    public async Task<IReadOnlyList<FederationProviderDto>> GetProvidersAsync(Guid? applicationSystemId, CancellationToken ct = default) =>
        (await ProviderQuery().AsNoTracking()
            .Where(item => !applicationSystemId.HasValue || item.ApplicationSystemId == applicationSystemId)
            .OrderBy(item => item.Name).ToListAsync(ct)).Select(Map).ToList();

    public Task<OperationResult<FederationProviderDto>> CreateProviderAsync(UpsertFederationProviderRequest request, CancellationToken ct = default) =>
        SaveProviderAsync(null, request, ct);

    public Task<OperationResult<FederationProviderDto>> UpdateProviderAsync(Guid id, UpsertFederationProviderRequest request, CancellationToken ct = default) =>
        SaveProviderAsync(id, request, ct);

    public async Task<OperationResult> DeleteProviderAsync(Guid id, CancellationToken ct = default)
    {
        var provider = await _db.FederationProviders.Include(x => x.ApplicationSystem).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (provider is null) return OperationResult.Failure("FEDERATION_PROVIDER_NOT_FOUND", "Federation provider not found.");
        _db.FederationProviders.Remove(provider);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("FEDERATION_PROVIDER_DELETED", applicationCode: provider.ApplicationSystem.Code, entityName: nameof(FederationProvider), entityId: id.ToString(), metadata: new { result = "Success" }, ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> CreateRoutingRuleAsync(CreateFederationRoutingRuleRequest request, CancellationToken ct = default)
    {
        if (request.Priority is < 1 or > 10000 ||
            (string.IsNullOrWhiteSpace(request.EmailDomain) && !request.DirectoryGroupId.HasValue && !request.ProfileAttributeDefinitionId.HasValue))
            return OperationResult.Failure("INVALID_ROUTING_RULE", "Priority and at least one domain, group, or profile condition are required.");
        var provider = await _db.FederationProviders.Include(x => x.ApplicationSystem).SingleOrDefaultAsync(item => item.Id == request.FederationProviderId, ct);
        if (provider is null)
            return OperationResult.Failure("FEDERATION_PROVIDER_NOT_FOUND", "Federation provider not found.");
        var validation = await ValidateRoutingRuleAsync(request.FederationProviderId, request.Priority, request.EmailDomain, request.DirectoryGroupId, request.ProfileAttributeDefinitionId, request.ExpectedProfileValueJson, null, ct);
        if (validation is not null)
            return OperationResult.Failure(validation.Value.Code == "FEDERATION_ROUTING_PRIORITY_EXISTS" ? "ROUTING_PRIORITY_EXISTS" : validation.Value.Code, validation.Value.Message);

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
        await _audit.LogAsync("FEDERATION_ROUTING_RULE_CREATED", applicationCode: provider.ApplicationSystem.Code, entityName: nameof(FederationRoutingRule), entityId: rule.Id.ToString(), metadata: new { result = "Success", rule.Priority }, ct: ct);
        return OperationResult.Success();
    }

    public async Task<IReadOnlyList<FederationRoutingRuleDto>> GetRoutingRulesAsync(Guid? applicationSystemId, CancellationToken ct = default)
    {
        var query = RoutingRuleQuery().AsNoTracking();
        if (applicationSystemId.HasValue) query = query.Where(x => x.FederationProvider.ApplicationSystemId == applicationSystemId);
        return (await query.OrderBy(x => x.Priority).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<OperationResult<FederationRoutingRuleDto>> UpdateRoutingRuleAsync(Guid id, UpdateFederationRoutingRuleRequest request, CancellationToken ct = default)
    {
        var rule = await _db.FederationRoutingRules.Include(x => x.FederationProvider).ThenInclude(x => x.ApplicationSystem).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (rule is null) return OperationResult<FederationRoutingRuleDto>.Failure("FEDERATION_ROUTING_RULE_NOT_FOUND", "Routing rule not found.");
        if (rule.Version != request.Version) return OperationResult<FederationRoutingRuleDto>.Failure("CONCURRENCY_CONFLICT", "The routing rule changed after it was loaded.");
        var validation = await ValidateRoutingRuleAsync(rule.FederationProviderId, request.Priority, request.EmailDomain, request.DirectoryGroupId, request.ProfileAttributeDefinitionId, request.ExpectedProfileValueJson, id, ct);
        if (validation is not null) return OperationResult<FederationRoutingRuleDto>.Failure(validation.Value.Code, validation.Value.Message);
        rule.Priority = request.Priority; rule.EmailDomain = NormalizeDomain(request.EmailDomain); rule.DirectoryGroupId = request.DirectoryGroupId; rule.ProfileAttributeDefinitionId = request.ProfileAttributeDefinitionId; rule.ExpectedProfileValueJson = request.ExpectedProfileValueJson; rule.IsActive = request.IsActive; rule.Version++;
        try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { return OperationResult<FederationRoutingRuleDto>.Failure("FEDERATION_ROUTING_PRIORITY_EXISTS", "Priority must be unique for the provider."); }
        await _audit.LogAsync("FEDERATION_ROUTING_RULE_UPDATED", applicationCode: rule.FederationProvider.ApplicationSystem.Code, entityName: nameof(FederationRoutingRule), entityId: id.ToString(), metadata: new { result = "Success", rule.Priority, rule.IsActive, rule.Version }, ct: ct);
        return OperationResult<FederationRoutingRuleDto>.Success(Map(await RoutingRuleQuery().AsNoTracking().SingleAsync(x => x.Id == id, ct)));
    }

    public async Task<OperationResult> ReorderRoutingRulesAsync(ReorderFederationRoutingRulesRequest request, CancellationToken ct = default)
    {
        if (request.Rules.Count == 0 || request.Rules.Select(x => x.Id).Distinct().Count() != request.Rules.Count || request.Rules.Select(x => x.Priority).Distinct().Count() != request.Rules.Count)
            return OperationResult.Failure("INVALID_FEDERATION_ROUTING_ORDER", "Rules and priorities must be unique.");
        var ids = request.Rules.Select(x => x.Id).ToArray(); var rules = await _db.FederationRoutingRules.Include(x => x.FederationProvider).ThenInclude(x => x.ApplicationSystem).Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        if (rules.Count != ids.Length) return OperationResult.Failure("FEDERATION_ROUTING_RULE_NOT_FOUND", "One or more routing rules were not found.");
        if (rules.Select(x => x.FederationProvider.ApplicationSystemId).Distinct().Count() != 1) return OperationResult.Failure("FEDERATION_ROUTING_APP_MISMATCH", "All reordered rules must belong to the same application.");
        foreach (var rule in rules) { var requested = request.Rules.Single(x => x.Id == rule.Id); if (rule.Version != requested.Version) return OperationResult.Failure("CONCURRENCY_CONFLICT", "A routing rule changed after it was loaded."); rule.Priority = -Math.Abs(requested.Priority) - 1; }
        await _db.SaveChangesAsync(ct);
        foreach (var rule in rules) { var requested = request.Rules.Single(x => x.Id == rule.Id); rule.Priority = requested.Priority; rule.Version++; }
        try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { return OperationResult.Failure("FEDERATION_ROUTING_PRIORITY_EXISTS", "Priorities must be unique for each provider."); }
        await _audit.LogAsync("FEDERATION_ROUTING_RULES_REORDERED", applicationCode: rules[0].FederationProvider.ApplicationSystem.Code, entityName: nameof(FederationRoutingRule), metadata: new { result = "Success", ruleIds = ids }, ct: ct); return OperationResult.Success();
    }

    public async Task<OperationResult> DeleteRoutingRuleAsync(Guid id, CancellationToken ct = default)
    {
        var rule = await _db.FederationRoutingRules.Include(x => x.FederationProvider).ThenInclude(x => x.ApplicationSystem).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (rule is null) return OperationResult.Failure("FEDERATION_ROUTING_RULE_NOT_FOUND", "Routing rule not found.");
        _db.Remove(rule); await _db.SaveChangesAsync(ct); await _audit.LogAsync("FEDERATION_ROUTING_RULE_DELETED", applicationCode: rule.FederationProvider.ApplicationSystem.Code, entityName: nameof(FederationRoutingRule), entityId: id.ToString(), metadata: new { result = "Success" }, ct: ct); return OperationResult.Success();
    }

    public async Task<OperationResult<FederationRouteResponse>> RouteAsync(FederationRouteRequest request, CancellationToken ct = default)
    {
        var applicationId = await _db.ApplicationSystems.AsNoTracking()
            .Where(item => item.Code == request.ApplicationCode && item.IsActive)
            .Select(item => (Guid?)item.Id).FirstOrDefaultAsync(ct);
        var domain = FederationDomains.OfEmail(request.Email);
        if (applicationId is null || domain is null)
            return OperationResult<FederationRouteResponse>.Failure("FEDERATION_ROUTE_NOT_FOUND", "No active federation route matched this application and identity.");

        var normalizedEmail = request.Email.Trim().ToUpperInvariant();
        var userId = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.NormalizedEmail == normalizedEmail && item.DeletedAt == null)
            .Select(item => (Guid?)item.Id).FirstOrDefaultAsync(ct);
        var rule = await MatchRuleAsync(applicationId.Value, domain, userId, evaluateDirectoryConditions: true, ct);
        return rule is null
            ? OperationResult<FederationRouteResponse>.Failure("FEDERATION_ROUTE_NOT_FOUND", "No active federation route matched this application and identity.")
            : OperationResult<FederationRouteResponse>.Success(new FederationRouteResponse
            {
                ProviderId = rule.FederationProviderId,
                ProviderName = rule.FederationProvider.Name,
                Protocol = rule.FederationProvider.Protocol.ToString(),
                MatchedRuleId = rule.Id,
                MatchedRulePriority = rule.Priority
            });
    }

    /// <summary>
    /// First active rule, in priority order, whose conditions hold. Group and profile conditions
    /// read directory data, so callers only evaluate them for an already identified user.
    /// </summary>
    private async Task<FederationRoutingRule?> MatchRuleAsync(Guid applicationId, string domain, Guid? userId, bool evaluateDirectoryConditions, CancellationToken ct)
    {
        var candidates = await _db.FederationRoutingRules.AsNoTracking()
            .Include(item => item.FederationProvider)
            .Where(item => item.IsActive && item.FederationProvider.IsActive && item.FederationProvider.ApplicationSystemId == applicationId)
            .OrderBy(item => item.Priority).ThenBy(item => item.FederationProviderId)
            .ToListAsync(ct);

        foreach (var rule in candidates)
        {
            var hasDirectoryCondition = rule.DirectoryGroupId.HasValue || rule.ProfileAttributeDefinitionId.HasValue;
            if (hasDirectoryCondition && (!evaluateDirectoryConditions || userId is null)) continue;
            if (rule.EmailDomain is not null && !string.Equals(rule.EmailDomain, domain, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.DirectoryGroupId.HasValue && !await _db.UserGroupMemberships.AnyAsync(item => item.UserId == userId && item.GroupId == rule.DirectoryGroupId && item.Group.IsActive, ct)) continue;
            if (rule.ProfileAttributeDefinitionId.HasValue && !await _db.UserProfileAttributeValues.AnyAsync(item => item.UserId == userId && item.AttributeDefinitionId == rule.ProfileAttributeDefinitionId && item.ValueJson == rule.ExpectedProfileValueJson, ct)) continue;
            return rule;
        }
        return null;
    }

    /// <summary>
    /// Maps a validated upstream identity to its AuthCenter user: an existing link by subject, or
    /// (with a verified email) account linking or just-in-time provisioning. Mapped groups are
    /// synchronised before access is checked, because a group can grant the application.
    /// </summary>
    private async Task<OperationResult<ApplicationUser>> ResolveFederatedUserAsync(FederationProvider provider, UpstreamIdentity identity, CancellationToken ct)
    {
        var providerKey = ProviderKey(provider);
        var existing = await _db.ExternalIdentityProviders.Include(item => item.User)
            .FirstOrDefaultAsync(item => item.Provider == providerKey && item.ProviderUserId == identity.Subject, ct);
        var link = existing is { IsActive: true } ? existing : null;
        ApplicationUser? user = link?.User;
        if (user is null)
        {
            // Linking to an existing account and creating one both trust the email address.
            if (!identity.EmailVerified)
                return OperationResult<ApplicationUser>.Failure("FEDERATION_EMAIL_NOT_VERIFIED", "The identity provider did not verify this email address.");
            user = await _users.FindByEmailAsync(identity.Email);
            if (user is not null && provider.AccountLinkingMode != AccountLinkingMode.VerifiedEmail)
                return OperationResult<ApplicationUser>.Failure("ACCOUNT_LINKING_REQUIRED", "An existing account requires an explicit linking policy.");
            if (user is null)
            {
                if (!provider.JitProvisioningEnabled) return OperationResult<ApplicationUser>.Failure("JIT_PROVISIONING_DISABLED", "No linked account exists and JIT provisioning is disabled.");
                user = new ApplicationUser { Id = Guid.NewGuid(), Email = identity.Email, UserName = identity.Email, FullName = identity.Name, EmailConfirmed = true, IsExternalUser = true, HasLocalPassword = false, IsActive = true, CreatedAt = _clock.UtcNow };
                var created = await _users.CreateAsync(user);
                if (!created.Succeeded) return OperationResult<ApplicationUser>.Failure("JIT_PROVISIONING_FAILED", string.Join("; ", created.Errors.Select(item => item.Description)));
                await _audit.LogAsync("FEDERATION_USER_JIT_PROVISIONED", user.Id, provider.ApplicationSystem.Code, nameof(FederationProvider), provider.Id.ToString(), ct: ct);
            }
            // An identity the user unlinked earlier keeps its row (the pair is unique): link it again.
            if (existing is not null)
            {
                existing.UserId = user.Id; existing.Email = identity.Email; existing.DisplayName = identity.Name;
                existing.LinkedAt = _clock.UtcNow; existing.LastUsedAt = _clock.UtcNow; existing.IsActive = true;
            }
            else
            {
                _db.ExternalIdentityProviders.Add(new ExternalIdentityProvider { Id = Guid.NewGuid(), UserId = user.Id, Provider = providerKey, ProviderUserId = identity.Subject, Email = identity.Email, DisplayName = identity.Name, LinkedAt = _clock.UtcNow, LastUsedAt = _clock.UtcNow, IsActive = true });
            }
        }
        else link!.LastUsedAt = _clock.UtcNow;

        if (!user.IsActive || user.DeletedAt is not null) return OperationResult<ApplicationUser>.Failure("USER_INACTIVE", "Linked user is inactive.");
        await _db.SaveChangesAsync(ct);
        await SyncGroupsAsync(provider, user.Id, identity.Groups, ct);

        if (!await _access.HasActiveAccessAsync(user.Id, provider.ApplicationSystemId, ct))
        {
            // Just-in-time access is only for identities that never had an assignment. A revoked or
            // pending-approval record is an administrative decision that federation must not undo.
            var hasAccessRecord = await _db.UserApplicationAccesses.AnyAsync(
                item => item.UserId == user.Id && item.ApplicationSystemId == provider.ApplicationSystemId, ct);
            if (hasAccessRecord)
            {
                await _audit.LogAsync("FEDERATION_ACCESS_DENIED", user.Id, provider.ApplicationSystem.Code, nameof(FederationProvider), provider.Id.ToString(), metadata: new { reason = "AccessRevokedOrPending" }, ct: ct);
                return OperationResult<ApplicationUser>.Failure("ACCESS_DENIED", "Access to this application was revoked or is pending approval.");
            }
            await _access.GrantAccessAsync(user.Id, provider.ApplicationSystemId, true, ct);
        }
        await _db.SaveChangesAsync(ct);
        return OperationResult<ApplicationUser>.Success(user);
    }

    /// <summary>
    /// Links an upstream identity to an existing account at that account's request (portal). The
    /// identity must not belong to another account; a previously unlinked row is reused. The
    /// account's owner is told by email, since a new way to sign in was added.
    /// </summary>
    private async Task<OperationResult<FederationProviderSummary>> LinkIdentityAsync(Guid providerId, Guid userId, string subject, string email, string name, FederationCaller caller, CancellationToken ct)
    {
        var provider = await _db.FederationProviders.AsNoTracking().Include(item => item.ApplicationSystem).FirstOrDefaultAsync(item => item.Id == providerId && item.IsActive, ct);
        if (provider is null)
            return OperationResult<FederationProviderSummary>.Failure("FEDERATION_PROVIDER_NOT_FOUND", "The identity provider is no longer available.");
        var user = await _users.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult<FederationProviderSummary>.Failure("USER_INACTIVE", "User account is inactive.");
        var providerKey = ProviderKey(provider);
        var existing = await _db.ExternalIdentityProviders.FirstOrDefaultAsync(item => item.Provider == providerKey && item.ProviderUserId == subject, ct);
        if (existing is { IsActive: true } && existing.UserId != userId)
        {
            await _audit.LogAsync("FEDERATION_LINK_REJECTED", userId, provider.ApplicationSystem.Code, nameof(FederationProvider), provider.Id.ToString(), caller.IpAddress, caller.UserAgent, new { reason = "IdentityInUse" }, ct);
            return OperationResult<FederationProviderSummary>.Failure("FEDERATION_IDENTITY_IN_USE", "This identity is already linked to another account.");
        }
        if (existing is null)
        {
            _db.ExternalIdentityProviders.Add(new ExternalIdentityProvider { Id = Guid.NewGuid(), UserId = userId, Provider = providerKey, ProviderUserId = subject, Email = email, DisplayName = name, LinkedAt = _clock.UtcNow, IsActive = true });
        }
        else
        {
            existing.UserId = userId; existing.Email = email; existing.DisplayName = name;
            existing.LinkedAt = _clock.UtcNow; existing.IsActive = true;
        }
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("FEDERATION_IDENTITY_LINKED", userId, provider.ApplicationSystem.Code, nameof(FederationProvider), provider.Id.ToString(), caller.IpAddress, caller.UserAgent, ct: ct);
        await _email.SendSecurityNoticeAsync(user.Email!, user.FullName, "Identity provider linked",
            $"You can now sign in with {provider.Name}. If this was not you, unlink it from your account portal and change your password.", ct);
        return OperationResult<FederationProviderSummary>.Success(new FederationProviderSummary { Id = provider.Id, Name = provider.Name, Protocol = provider.Protocol.ToString() });
    }

    private static string ProviderKey(FederationProvider provider) => $"Federation:{provider.Id:N}";

    /// <summary>
    /// The provider is authoritative for its mapped groups: memberships follow the upstream values
    /// on every sign-in. Like an administrative membership change, a change closes the user's
    /// existing sessions so no token keeps roles or access the user no longer has.
    /// </summary>
    private async Task SyncGroupsAsync(FederationProvider provider, Guid userId, IReadOnlyCollection<string>? upstreamGroups, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider.GroupsClaim) || upstreamGroups is null)
            return;
        var mappings = await _db.FederationGroupMappings.AsNoTracking()
            .Where(item => item.FederationProviderId == provider.Id && item.DirectoryGroup.IsActive)
            .Select(item => new { item.UpstreamValue, item.DirectoryGroupId })
            .ToListAsync(ct);
        if (mappings.Count == 0)
            return;

        var values = new HashSet<string>(upstreamGroups, StringComparer.OrdinalIgnoreCase);
        var desired = mappings.Where(item => values.Contains(item.UpstreamValue)).Select(item => item.DirectoryGroupId).ToHashSet();
        var managed = mappings.Select(item => item.DirectoryGroupId).Distinct().ToList();
        var current = await _db.UserGroupMemberships.Where(item => item.UserId == userId && managed.Contains(item.GroupId)).ToListAsync(ct);
        var added = desired.Where(groupId => current.All(item => item.GroupId != groupId)).ToList();
        var removed = current.Where(item => !desired.Contains(item.GroupId)).ToList();
        if (added.Count == 0 && removed.Count == 0)
            return;

        await _refreshTokens.RevokeAllForUserAsync(userId, ct);
        foreach (var groupId in added)
            _db.UserGroupMemberships.Add(new UserGroupMembership { GroupId = groupId, UserId = userId, CreatedAt = _clock.UtcNow });
        _db.UserGroupMemberships.RemoveRange(removed);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("FEDERATION_GROUPS_SYNCED", userId, provider.ApplicationSystem.Code, nameof(FederationProvider), provider.Id.ToString(),
            metadata: new { added, removed = removed.Select(item => item.GroupId).ToList() }, ct: ct);
    }

    private async Task<OperationResult<FederationProviderDto>> SaveProviderAsync(Guid? id, UpsertFederationProviderRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<FederationProtocol>(request.Protocol, true, out var protocol) || !Enum.IsDefined(protocol) ||
            !Enum.TryParse<AccountLinkingMode>(request.AccountLinkingMode, true, out var linking) || !Enum.IsDefined(linking) ||
            string.IsNullOrWhiteSpace(request.Name))
            return OperationResult<FederationProviderDto>.Failure("INVALID_FEDERATION_PROVIDER", "Name, issuer, protocol, and linking mode are required.");
        var oidc = protocol == FederationProtocol.Oidc;
        var issuer = NormalizeIssuer(request.Issuer, oidc);
        if (issuer is null)
            return OperationResult<FederationProviderDto>.Failure("INVALID_FEDERATION_PROVIDER", oidc
                ? "An OIDC issuer must be an absolute HTTPS URL without query or fragment."
                : "A SAML issuer (entity ID) must be an absolute URI.");
        if (!await _db.ApplicationSystems.AnyAsync(item => item.Id == request.ApplicationSystemId && item.IsActive, ct))
            return OperationResult<FederationProviderDto>.Failure("APP_NOT_FOUND", "Active application not found.");
        // Without an explicit value the OIDC callback is AuthCenter's own hosted callback.
        var callback = !oidc ? null : string.IsNullOrWhiteSpace(request.OidcCallbackUrl) ? HostedOidcCallbackUrl : request.OidcCallbackUrl.Trim();
        if (oidc && (string.IsNullOrWhiteSpace(request.ClientId) || !IsHttps(callback)))
            return OperationResult<FederationProviderDto>.Failure("INVALID_OIDC_PROVIDER", "OIDC client ID and exact HTTPS callback URL are required.");
        if (oidc && !string.IsNullOrWhiteSpace(request.DiscoveryEndpoint) && !IsHttps(request.DiscoveryEndpoint.Trim()))
            return OperationResult<FederationProviderDto>.Failure("INVALID_OIDC_PROVIDER", "The OIDC discovery endpoint must be an HTTPS URL.");

        var groupsClaim = string.IsNullOrWhiteSpace(request.GroupsClaim) ? null : request.GroupsClaim.Trim();
        var mappings = (request.GroupMappings ?? []).Select(item => (Value: item.UpstreamValue?.Trim() ?? string.Empty, GroupId: item.DirectoryGroupId)).ToList();
        if (groupsClaim is { Length: > 256 } || mappings.Count > MaximumGroupMappings ||
            mappings.Any(item => item.Value.Length is 0 or > 256 || item.GroupId == Guid.Empty) ||
            mappings.Select(item => (item.Value.ToUpperInvariant(), item.GroupId)).Distinct().Count() != mappings.Count)
            return OperationResult<FederationProviderDto>.Failure("INVALID_GROUP_MAPPING", $"Each group mapping needs an upstream value (at most 256 characters) and a group, without repetitions; at most {MaximumGroupMappings} mappings.");
        if (mappings.Count > 0 && groupsClaim is null)
            return OperationResult<FederationProviderDto>.Failure("INVALID_GROUP_MAPPING", "Set the groups claim before mapping its values to groups.");
        var mappedGroups = mappings.Select(item => item.GroupId).Distinct().ToList();
        if (mappedGroups.Count > 0 && await _db.DirectoryGroups.CountAsync(item => mappedGroups.Contains(item.Id) && item.IsActive, ct) != mappedGroups.Count)
            return OperationResult<FederationProviderDto>.Failure("GROUP_NOT_FOUND", "Every mapped group must be an active directory group.");

        var provider = id.HasValue ? await _db.FederationProviders.Include(item => item.GroupMappings).SingleOrDefaultAsync(item => item.Id == id.Value, ct) : null;
        if (id.HasValue && provider is null) return OperationResult<FederationProviderDto>.Failure("FEDERATION_PROVIDER_NOT_FOUND", "Federation provider not found.");
        if (id.HasValue && provider!.Version != request.Version) return OperationResult<FederationProviderDto>.Failure("CONCURRENCY_CONFLICT", "The federation provider changed after it was loaded.");
        // Like the client secret, a blank certificate on update keeps the stored one; the DTO only exposes its thumbprint.
        var certificatePem = protocol != FederationProtocol.Saml2 ? null : string.IsNullOrWhiteSpace(request.SamlSigningCertificatePem) ? provider?.SamlSigningCertificatePem : request.SamlSigningCertificatePem;
        if (protocol == FederationProtocol.Saml2 && (!IsHttps(request.SamlSingleSignOnUrl) || !TryCertificate(certificatePem, out _)))
            return OperationResult<FederationProviderDto>.Failure("INVALID_SAML_PROVIDER", "SAML HTTPS SSO URL and a valid signing certificate are required.");
        provider ??= new FederationProvider { Id = Guid.NewGuid(), CreatedAt = _clock.UtcNow };
        provider.ApplicationSystemId = request.ApplicationSystemId; provider.Name = request.Name.Trim(); provider.Protocol = protocol; provider.Issuer = issuer;
        // A provider never carries settings of the protocol it does not use, even if the request or a previous version had them.
        provider.DiscoveryEndpoint = oidc && !string.IsNullOrWhiteSpace(request.DiscoveryEndpoint) ? request.DiscoveryEndpoint.Trim() : null; provider.ClientId = oidc ? request.ClientId?.Trim() : null; provider.OidcCallbackUrl = callback;
        if (!oidc) provider.ProtectedClientSecret = null; else if (!string.IsNullOrWhiteSpace(request.ClientSecret)) provider.ProtectedClientSecret = _secrets.Protect(request.ClientSecret);
        provider.SamlSingleSignOnUrl = oidc ? null : request.SamlSingleSignOnUrl?.Trim(); provider.SamlSigningCertificatePem = certificatePem?.Trim();
        provider.JitProvisioningEnabled = request.JitProvisioningEnabled; provider.AccountLinkingMode = linking; provider.IsActive = request.IsActive; provider.UpdatedAt = id.HasValue ? _clock.UtcNow : null;
        provider.RequireVerifiedEmail = !oidc || request.RequireVerifiedEmail;
        provider.TrustUpstreamMfa = request.TrustUpstreamMfa;
        provider.GroupsClaim = groupsClaim;
        foreach (var stale in provider.GroupMappings.Where(existing => !mappings.Any(item => item.GroupId == existing.DirectoryGroupId && string.Equals(item.Value, existing.UpstreamValue, StringComparison.OrdinalIgnoreCase))).ToList())
            _db.FederationGroupMappings.Remove(stale);
        foreach (var (value, groupId) in mappings.Where(item => !provider.GroupMappings.Any(existing => existing.DirectoryGroupId == item.GroupId && string.Equals(item.Value, existing.UpstreamValue, StringComparison.OrdinalIgnoreCase))))
            _db.FederationGroupMappings.Add(new FederationGroupMapping { Id = Guid.NewGuid(), FederationProviderId = provider.Id, UpstreamValue = value, DirectoryGroupId = groupId });
        if (id.HasValue) provider.Version++;
        if (!id.HasValue) _db.FederationProviders.Add(provider);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return OperationResult<FederationProviderDto>.Failure("FEDERATION_PROVIDER_EXISTS", "A provider with this name already exists for the application."); }
        var applicationCode = await _db.ApplicationSystems.Where(x => x.Id == provider.ApplicationSystemId).Select(x => x.Code).SingleAsync(ct);
        await _audit.LogAsync(id.HasValue ? "FEDERATION_PROVIDER_UPDATED" : "FEDERATION_PROVIDER_CREATED", applicationCode: applicationCode, entityName: nameof(FederationProvider), entityId: provider.Id.ToString(), metadata: new { result = "Success", protocol = provider.Protocol.ToString(), provider.IsActive, provider.Version, hasClientSecret = provider.ProtectedClientSecret is not null, provider.TrustUpstreamMfa, provider.RequireVerifiedEmail, groupMappings = mappings.Count }, ct: ct);
        return OperationResult<FederationProviderDto>.Success(Map(await ProviderQuery().AsNoTracking().SingleAsync(item => item.Id == provider.Id, ct)));
    }

    private IQueryable<FederationProvider> ProviderQuery() =>
        _db.FederationProviders.Include(item => item.GroupMappings).ThenInclude(item => item.DirectoryGroup);

    private IQueryable<FederationRoutingRule> RoutingRuleQuery() => _db.FederationRoutingRules.Include(x => x.FederationProvider).ThenInclude(x => x.ApplicationSystem);

    private async Task<(string Code, string Message)?> ValidateRoutingRuleAsync(Guid providerId, int priority, string? domain, Guid? groupId, Guid? definitionId, string? valueJson, Guid? excludedId, CancellationToken ct)
    {
        if (priority < 0 || !await _db.FederationProviders.AnyAsync(x => x.Id == providerId, ct)) return ("INVALID_FEDERATION_ROUTING_RULE", "An existing provider and non-negative priority are required.");
        if (!string.IsNullOrWhiteSpace(domain) && (domain.Contains('@', StringComparison.Ordinal) || FederationDomains.Normalize(domain) is null)) return ("INVALID_EMAIL_DOMAIN", "Email domain must be a domain name without @.");
        if (groupId.HasValue && !await _db.DirectoryGroups.AnyAsync(x => x.Id == groupId && x.IsActive, ct)) return ("GROUP_NOT_FOUND", "Active group not found.");
        if (definitionId.HasValue && (string.IsNullOrWhiteSpace(valueJson) || !await _db.UserProfileAttributeDefinitions.AnyAsync(x => x.Id == definitionId && x.IsActive, ct))) return ("INVALID_PROFILE_CONDITION", "Active profile definition and expected value are required together.");
        if (await _db.FederationRoutingRules.AnyAsync(x => x.FederationProviderId == providerId && x.Priority == priority && x.Id != excludedId, ct)) return ("FEDERATION_ROUTING_PRIORITY_EXISTS", "Priority must be unique for the provider.");
        return null;
    }

    private static FederationRoutingRuleDto Map(FederationRoutingRule x) => new() { Id = x.Id, FederationProviderId = x.FederationProviderId, ProviderName = x.FederationProvider.Name, ApplicationSystemId = x.FederationProvider.ApplicationSystemId, Priority = x.Priority, EmailDomain = x.EmailDomain, DirectoryGroupId = x.DirectoryGroupId, ProfileAttributeDefinitionId = x.ProfileAttributeDefinitionId, ExpectedProfileValueJson = x.ExpectedProfileValueJson, IsActive = x.IsActive, Version = x.Version };

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
        RequireVerifiedEmail = item.RequireVerifiedEmail,
        TrustUpstreamMfa = item.TrustUpstreamMfa,
        GroupsClaim = item.GroupsClaim,
        GroupMappings = item.GroupMappings
            .OrderBy(mapping => mapping.UpstreamValue, StringComparer.OrdinalIgnoreCase)
            .Select(mapping => new FederationGroupMappingDto
            {
                UpstreamValue = mapping.UpstreamValue,
                DirectoryGroupId = mapping.DirectoryGroupId,
                DirectoryGroupName = mapping.DirectoryGroup?.Name
            }).ToList(),
        IsActive = item.IsActive,
        Version = item.Version
    };

    /// <summary>
    /// OIDC issuers are HTTPS URLs; SAML entity IDs may be any absolute URI (https, http or urn).
    /// The value is kept as written, because upstream issuers are compared as exact strings.
    /// </summary>
    private static string? NormalizeIssuer(string? value, bool oidc)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 500 || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return null;
        if (!oidc)
            return trimmed;
        return uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) ? trimmed : null;
    }

    private static bool IsHttps(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.Fragment);
    private static string? NormalizeDomain(string? value) => FederationDomains.Normalize(value);
    private static bool FixedEquals(string? left, string? right) => left is not null && right is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private static string? HashBinding(string? binding) => string.IsNullOrWhiteSpace(binding) ? null : Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(binding)));
    private static bool TryCertificate(string? pem, out X509Certificate2? certificate) { certificate = null; try { if (string.IsNullOrWhiteSpace(pem)) return false; certificate = X509Certificate2.CreateFromPem(pem); return true; } catch (CryptographicException) { return false; } }

    /// <summary>An upstream identity after protocol validation.</summary>
    /// <param name="EmailVerified">The email may be used to link or create an account.</param>
    /// <param name="MultiFactor">The upstream reported a multi-factor authentication.</param>
    /// <param name="Groups">Values of the provider's groups claim; <c>null</c> when not configured or not usable.</param>
    private sealed record UpstreamIdentity(string Subject, string Email, string Name, bool EmailVerified, bool MultiFactor, IReadOnlyCollection<string>? Groups);
}
