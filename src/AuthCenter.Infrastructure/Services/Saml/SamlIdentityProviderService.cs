using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Responses.OAuth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services.Saml;

/// <summary>
/// AuthCenter as a SAML 2.0 identity provider (Web Browser SSO profile). A request is trusted only
/// from a registered, active service provider, at one of its assertion consumer service URLs, signed
/// when the provider requires it, fresh and used once; the browser's single sign-on session then
/// passes the same access gate as OpenID Connect (access, policy, MFA). Until the request can be
/// trusted it is answered with an error page, never with a post to an unverified address.
/// </summary>
public sealed class SamlIdentityProviderService : ISamlIdentityProviderService
{
    private const string InteractionPurpose = "saml_idp_interaction";
    private const string ResponsePurpose = "saml_idp_response";
    private const string RequestIdPurpose = "saml_idp_request_id";
    private const string PostedPurpose = "saml_idp_posted";
    private static readonly TimeSpan InteractionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(5);

    private readonly AuthCenterDbContext _db;
    private readonly ISsoAccessGate _gate;
    private readonly ITransientStateStore _state;
    private readonly IRoleService _roles;
    private readonly IUserProfileService _profiles;
    private readonly ISingleSignOnSessionService _sessions;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;
    private readonly SamlIdentityProviderKeys _keys;

    public SamlIdentityProviderService(
        AuthCenterDbContext db,
        ISsoAccessGate gate,
        ITransientStateStore state,
        IRoleService roles,
        IUserProfileService profiles,
        ISingleSignOnSessionService sessions,
        IAuditService audit,
        IDateTimeProvider clock,
        SamlIdentityProviderKeys keys)
    {
        _db = db;
        _gate = gate;
        _state = state;
        _roles = roles;
        _profiles = profiles;
        _sessions = sessions;
        _audit = audit;
        _clock = clock;
        _keys = keys;
    }

    public SamlIdentityProviderInfo Describe()
    {
        var usable = _keys.SigningCertificate(out var problem);
        var certificate = _keys.ConfiguredCertificate;
        return new SamlIdentityProviderInfo(
            usable is not null,
            _keys.EntityId,
            _keys.MetadataUrl,
            _keys.SingleSignOnUrl,
            _keys.SingleLogoutUrl,
            certificate is null ? null : certificate.ExportCertificatePem(),
            certificate is null ? null : Convert.ToHexString(SHA256.HashData(certificate.RawData)),
            certificate?.NotBefore.ToUniversalTime(),
            certificate?.NotAfter.ToUniversalTime(),
            problem);
    }

    public string? Metadata() =>
        _keys.SigningCertificate(out _) is { } certificate
            ? SamlIdpMessages.BuildMetadata(_keys.EntityId, _keys.SingleSignOnUrl, _keys.SingleLogoutUrl, certificate)
            : null;

    public async Task<string> KeepPostedMessageAsync(string samlMessage, string? relayState, CancellationToken ct = default)
    {
        var key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        await _state.SetAsync(PostedPurpose, key, JsonSerializer.Serialize(new PostedMessage(samlMessage, relayState)), _clock.UtcNow.Add(RequestLifetime), ct);
        return key;
    }

    public async Task<(string SamlMessage, string? RelayState)?> TakePostedMessageAsync(string key, CancellationToken ct = default)
    {
        var stored = await _state.TakeAsync(PostedPurpose, key, ct);
        if (stored is null)
            return null;
        var message = JsonSerializer.Deserialize<PostedMessage>(stored)!;
        return (message.SamlMessage, message.RelayState);
    }

    public async Task<SamlEndpointOutcome> SignInAsync(SamlSignInStart start, AuthorizationCaller caller, CancellationToken ct = default)
    {
        if (_keys.SigningCertificate(out _) is null)
            return SamlEndpointOutcome.Error(503, "AuthCenter is not configured as a SAML identity provider.");
        var now = _clock.UtcNow;
        SamlServiceProvider? provider;
        SamlInboundRequest? request = null;
        string assertionConsumerService;
        string? relayState = start.RelayState;

        if (start.Binding == SamlRequestBinding.IdpInitiated)
        {
            provider = await _db.SamlServiceProviders.AsNoTracking().Include(item => item.ApplicationSystem)
                .SingleOrDefaultAsync(item => item.Id == start.ServiceProviderId && item.IsActive && item.AllowIdpInitiated && item.ApplicationSystem.IsActive, ct);
            if (provider is null)
                return SamlEndpointOutcome.Error(404, "This application cannot be opened from AuthCenter.");
            assertionConsumerService = AssertionConsumerServices(provider)[0];
            relayState ??= provider.DefaultRelayState;
        }
        else
        {
            var checkedRequest = await ReadTrustedRequestAsync(start, "AuthnRequest", ct);
            if (checkedRequest.Error is { } error)
                return error;
            (request, provider) = (checkedRequest.Request!, checkedRequest.Provider!);
            var registered = AssertionConsumerServices(provider);
            // The response only goes to an address the provider registered.
            if (request.AssertionConsumerServiceUrl is { } requested && !registered.Contains(requested, StringComparer.Ordinal))
                return await RejectAsync(provider, "UnregisteredAssertionConsumerService", "The assertion consumer service URL is not registered for this application.", ct);
            if (request.ProtocolBinding is { } binding && binding != SamlIdpMessages.PostBinding)
                return await RejectAsync(provider, "UnsupportedBinding", "Responses are only sent with the HTTP-POST binding.", ct);
            assertionConsumerService = request.AssertionConsumerServiceUrl ?? registered[0];
            if (!await _state.TryConsumeAsync(RequestIdPurpose, $"{provider.Id:N}:{request.Id}", now.Add(InteractionLifetime), ct))
                return await RejectAsync(provider, "ReplayedRequest", "This sign-in request was already used.", ct);
        }

        // From here the request is trusted: failures are answered to the service provider.
        var nameIdFormat = NameIdFormat(provider, request?.NameIdPolicyFormat);
        if (nameIdFormat is null)
            return SamlEndpointOutcome.Posted(await ErrorResponseAsync(provider, request, assertionConsumerService, relayState, SamlIdpMessages.InvalidNameIdPolicy, "The requested name identifier format is not supported.", null, ct));
        var interaction = new SamlInteraction(
            provider.Id, provider.ApplicationSystemId, request?.Id, assertionConsumerService, relayState,
            request?.ForceAuthn == true, request?.IsPassive == true, nameIdFormat,
            request?.RequestedAuthnContextClasses.Any(SamlIdpMessages.MultiFactorContextClasses.Contains) == true ? AuthenticationAssuranceLevel.Mfa : null,
            request?.RequestedAuthnContextClasses.FirstOrDefault(SamlIdpMessages.MultiFactorContextClasses.Contains),
            LoginHint(request), Hash(caller.BrowserBinding ?? string.Empty), now);

        var session = await _gate.ResolveSessionAsync(caller.UserId, caller.SessionId, ct);
        if (session is null || interaction.ForceAuthn && session.AuthenticatedAt < interaction.CreatedAt)
        {
            if (interaction.IsPassive)
                return SamlEndpointOutcome.Posted(await ErrorResponseAsync(provider, request, assertionConsumerService, relayState, SamlIdpMessages.NoPassive, "Signing in needs the user.", null, ct));
            return SamlEndpointOutcome.Redirect(await ContinueInHostedLoginAsync(interaction, ct));
        }

        var decision = await _gate.EvaluateAsync(session, provider.ApplicationSystemId, interaction.RequestedAssurance, caller.IpAddress, caller.UserAgent, ct);
        return decision.Outcome switch
        {
            SsoAccessOutcome.Allowed => SamlEndpointOutcome.Posted(await IssueAsync(provider, interaction, session, ct)),
            SsoAccessOutcome.StepUp when interaction.IsPassive => SamlEndpointOutcome.Posted(await ErrorResponseAsync(provider, request, assertionConsumerService, relayState, SamlIdpMessages.NoPassive, "A stronger sign-in needs the user.", session.UserId, ct)),
            SsoAccessOutcome.StepUp => SamlEndpointOutcome.Redirect(await ContinueInHostedLoginAsync(interaction, ct)),
            _ => SamlEndpointOutcome.Posted(await DenyAsync(provider, interaction, session.UserId, decision, ct))
        };
    }

    public async Task<OperationResult<OAuthInteractionContextResponse>> GetInteractionContextAsync(string interactionId, string? browserBinding, CancellationToken ct = default)
    {
        var interaction = await ReadInteractionAsync(interactionId, browserBinding, ct);
        if (interaction is null)
            return OperationResult<OAuthInteractionContextResponse>.Failure("INVALID_INTERACTION", "Interaction not found or expired.");
        var provider = await ActiveProviderAsync(interaction.ServiceProviderId, ct);
        if (provider is null)
            return OperationResult<OAuthInteractionContextResponse>.Failure("INVALID_CLIENT", "The SAML application is no longer active.");
        var settings = await _db.ApplicationRegistrationSettings.AsNoTracking().FirstOrDefaultAsync(item => item.ApplicationSystemId == provider.ApplicationSystemId, ct);
        var federation = await _db.FederationProviders.AsNoTracking().AnyAsync(item => item.ApplicationSystemId == provider.ApplicationSystemId && item.IsActive, ct);
        return OperationResult<OAuthInteractionContextResponse>.Success(new OAuthInteractionContextResponse
        {
            ApplicationCode = provider.ApplicationSystem.Code,
            ApplicationName = provider.ApplicationSystem.Name,
            ClientDisplayName = provider.Name,
            LoginHint = interaction.LoginHint,
            RequiresFreshLogin = interaction.ForceAuthn,
            AllowPasswordLogin = settings?.AllowPasswordLogin ?? true,
            AllowMagicLink = settings?.AllowMagicLink ?? false,
            FederationAvailable = federation,
            ExpiresAt = DateTime.SpecifyKind(interaction.CreatedAt, DateTimeKind.Utc).Add(InteractionLifetime)
        });
    }

    public async Task<OperationResult<StepUpRequirement>> GetStepUpRequirementAsync(string interactionId, AuthorizationCaller caller, CancellationToken ct = default)
    {
        var interaction = await ReadInteractionAsync(interactionId, caller.BrowserBinding, ct);
        if (interaction is null)
            return OperationResult<StepUpRequirement>.Failure("INVALID_INTERACTION", "Interaction not found or expired.");
        var provider = await ActiveProviderAsync(interaction.ServiceProviderId, ct);
        if (provider is null)
            return OperationResult<StepUpRequirement>.Failure("INVALID_CLIENT", "The SAML application is no longer active.");
        var session = await _gate.ResolveSessionAsync(caller.UserId, caller.SessionId, ct);
        if (session is null)
            return OperationResult<StepUpRequirement>.Failure("LOGIN_REQUIRED", "Sign in to continue.");
        var decision = await _gate.EvaluateAsync(session, provider.ApplicationSystemId, interaction.RequestedAssurance, caller.IpAddress, caller.UserAgent, ct);
        if (decision.Outcome == SsoAccessOutcome.Denied)
            return OperationResult<StepUpRequirement>.Failure("ACCESS_DENIED", "The application's access policy does not allow this sign-in.");
        return OperationResult<StepUpRequirement>.Success(new StepUpRequirement(
            provider.ApplicationSystem.Code,
            decision.Outcome == SsoAccessOutcome.StepUp ? decision.RequiredAssurance : null,
            session.Methods.FirstOrDefault() ?? DomainConstants.AuthenticationMethods.Password));
    }

    public async Task<OperationResult<string>> CompleteInteractionAsync(string interactionId, AuthorizationCaller caller, CancellationToken ct = default)
    {
        var interaction = await ReadInteractionAsync(interactionId, caller.BrowserBinding, ct);
        if (interaction is null)
            return OperationResult<string>.Failure("INVALID_INTERACTION", "Interaction not found or expired.");
        var provider = await ActiveProviderAsync(interaction.ServiceProviderId, ct);
        if (provider is null)
            return OperationResult<string>.Failure("INVALID_CLIENT", "The SAML application is no longer active.");
        var session = await _gate.ResolveSessionAsync(caller.UserId, caller.SessionId, ct);
        // ForceAuthn is met by a sign-in made after the request arrived.
        if (session is null || interaction.ForceAuthn && session.AuthenticatedAt < interaction.CreatedAt)
            return OperationResult<string>.Failure("LOGIN_REQUIRED", "Sign in to continue.");
        var decision = await _gate.EvaluateAsync(session, provider.ApplicationSystemId, interaction.RequestedAssurance, caller.IpAddress, caller.UserAgent, ct);
        if (decision.Outcome == SsoAccessOutcome.StepUp)
            return OperationResult<string>.Failure("STEP_UP_REQUIRED", "The application requires a stronger sign-in.");
        if (await _state.TakeAsync(InteractionPurpose, interactionId, ct) is null)
            return OperationResult<string>.Failure("INVALID_INTERACTION", "Interaction not found or expired.");
        var message = decision.Outcome == SsoAccessOutcome.Allowed
            ? await IssueAsync(provider, interaction, session, ct)
            : await DenyAsync(provider, interaction, session.UserId, decision, ct);
        var responseId = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        await _state.SetAsync(ResponsePurpose, responseId, JsonSerializer.Serialize(new PendingResponse(message.Destination, message.Fields.ToDictionary(), interaction.BindingHash)), _clock.UtcNow.AddMinutes(2), ct);
        return OperationResult<string>.Success(responseId);
    }

    public async Task<SamlPostMessage?> TakePendingResponseAsync(string responseId, string? browserBinding, CancellationToken ct = default)
    {
        var stored = await _state.TakeAsync(ResponsePurpose, responseId, ct);
        if (stored is null)
            return null;
        var pending = JsonSerializer.Deserialize<PendingResponse>(stored)!;
        return BindingMatches(pending.BindingHash, browserBinding) ? new SamlPostMessage(pending.Destination, pending.Fields.ToList()) : null;
    }

    public async Task<SamlEndpointOutcome> LogoutAsync(SamlSignInStart start, AuthorizationCaller caller, CancellationToken ct = default)
    {
        if (_keys.SigningCertificate(out _) is not { } certificate)
            return SamlEndpointOutcome.Error(503, "AuthCenter is not configured as a SAML identity provider.");
        var checkedRequest = await ReadTrustedRequestAsync(start, "LogoutRequest", ct);
        if (checkedRequest.Error is { } error)
            return error;
        var (request, provider) = (checkedRequest.Request!, checkedRequest.Provider!);
        if (provider.SingleLogoutServiceUrl is not { } destination)
            return await RejectAsync(provider, "NoSingleLogoutService", "This application has no single logout URL registered.", ct);

        // The session the provider names (SessionIndex) or, failing that, the browser's, if the
        // name identifier is the one issued to this provider for its user.
        var ended = false;
        var endedBrowserSession = false;
        var candidates = new List<Guid>();
        if (Guid.TryParse(request.SessionIndex, out var sessionIndex))
            candidates.Add(sessionIndex);
        if (caller.SessionId is { } browserSession && !candidates.Contains(browserSession))
            candidates.Add(browserSession);
        foreach (var sessionId in candidates)
        {
            var owner = await _db.RefreshTokens.AsNoTracking()
                .Where(token => token.Id == sessionId && token.OAuthClientId == null && token.RevokedAt == null)
                .Select(token => token.User)
                .SingleOrDefaultAsync(ct);
            if (owner is null || request.NameId is null || !SamlNameIdFormats.Supported.Any(format => NameIdFor(provider, owner, format) == request.NameId))
                continue;
            ended |= (await _sessions.EndSessionAsync(owner.Id, sessionId, "saml_logout", ct)).IsSuccess;
            endedBrowserSession |= sessionId == caller.SessionId;
            await _audit.LogAsync("SAML_LOGOUT_COMPLETED", owner.Id, applicationCode: provider.ApplicationSystem.Code, entityName: nameof(SamlServiceProvider), entityId: provider.Id.ToString(), metadata: new { provider.EntityId, sessionId }, ct: ct);
            break;
        }
        // Success either way: a session already ended is logged out.
        var response = SamlIdpMessages.BuildLogoutResponse(_keys.EntityId, destination, request.Id, _clock.UtcNow, succeeded: true, certificate);
        var fields = new List<KeyValuePair<string, string>> { new("SAMLResponse", response) };
        if (!string.IsNullOrEmpty(start.RelayState))
            fields.Add(new("RelayState", start.RelayState));
        return SamlEndpointOutcome.Posted(new SamlPostMessage(destination, fields), endedBrowserSession || ended && caller.SessionId is null);
    }

    /// <summary>Reads a request, finds its active service provider and checks signature, freshness and destination.</summary>
    private async Task<(SamlInboundRequest? Request, SamlServiceProvider? Provider, SamlEndpointOutcome? Error)> ReadTrustedRequestAsync(SamlSignInStart start, string kind, CancellationToken ct)
    {
        var request = string.IsNullOrEmpty(start.SamlRequest)
            ? null
            : start.Binding == SamlRequestBinding.Redirect ? SamlIdpMessages.ReadRedirect(start.SamlRequest) : SamlIdpMessages.ReadPost(start.SamlRequest);
        if (request is null || request.Kind != kind)
        {
            await _audit.LogAsync("SAML_REQUEST_REJECTED", metadata: new { reason = "Unreadable", kind }, ct: ct);
            return (null, null, SamlEndpointOutcome.Error(400, "The SAML request could not be read."));
        }
        var provider = request.Issuer is null
            ? null
            : await _db.SamlServiceProviders.AsNoTracking().Include(item => item.ApplicationSystem)
                .SingleOrDefaultAsync(item => item.EntityId == request.Issuer && item.IsActive && item.ApplicationSystem.IsActive, ct);
        if (provider is null)
        {
            await _audit.LogAsync("SAML_REQUEST_REJECTED", metadata: new { reason = "UnknownServiceProvider", issuer = Bound(request.Issuer, 300) }, ct: ct);
            return (null, null, SamlEndpointOutcome.Error(400, "The application that sent this request is not registered in AuthCenter."));
        }

        var signingCertificate = Certificate(provider.SigningCertificate);
        var signed = start.Binding == SamlRequestBinding.Redirect
            ? start.RawQuery?.Contains("Signature=", StringComparison.Ordinal) == true
            : request.Document.DocumentElement!.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#").Count > 0;
        if (provider.RequireSignedRequests || signed)
        {
            // A signature that is present is always checked, even when the provider need not sign.
            var valid = signingCertificate is not null && (start.Binding == SamlRequestBinding.Redirect
                ? SamlIdpMessages.VerifyRedirectSignature(start.RawQuery ?? string.Empty, signingCertificate)
                : SamlIdpMessages.VerifyXmlSignature(request.Document, signingCertificate));
            if (!valid)
                return (null, null, await RejectAsync(provider, "InvalidSignature", "The request's signature could not be verified.", ct));
        }
        var now = _clock.UtcNow;
        if (request.IssueInstant is not { } issued || issued > now.Add(_keys.ClockSkew) || issued < now.Subtract(RequestLifetime).Subtract(_keys.ClockSkew))
            return (null, null, await RejectAsync(provider, "StaleRequest", "The request is too old or dated in the future.", ct));
        if (request.Destination is { } destination &&
            !string.Equals(destination, kind == "LogoutRequest" ? _keys.SingleLogoutUrl : _keys.SingleSignOnUrl, StringComparison.OrdinalIgnoreCase))
            return (null, null, await RejectAsync(provider, "WrongDestination", "The request was addressed to another endpoint.", ct));
        return (request, provider, null);
    }

    private async Task<SamlEndpointOutcome> RejectAsync(SamlServiceProvider provider, string reason, string message, CancellationToken ct)
    {
        await _audit.LogAsync("SAML_REQUEST_REJECTED", applicationCode: provider.ApplicationSystem.Code, entityName: nameof(SamlServiceProvider), entityId: provider.Id.ToString(), metadata: new { reason, provider.EntityId }, ct: ct);
        return SamlEndpointOutcome.Error(400, message);
    }

    private async Task<string> ContinueInHostedLoginAsync(SamlInteraction interaction, CancellationToken ct)
    {
        var id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        await _state.SetAsync(InteractionPurpose, id, JsonSerializer.Serialize(interaction), interaction.CreatedAt.Add(InteractionLifetime), ct);
        return $"/login?saml_interaction={Uri.EscapeDataString(id)}";
    }

    private async Task<SamlPostMessage> IssueAsync(SamlServiceProvider provider, SamlInteraction interaction, SsoSessionInfo session, CancellationToken ct)
    {
        var certificate = _keys.SigningCertificate(out _) ?? throw new InvalidOperationException("The SAML certificate is not available.");
        var user = await _db.Users.AsNoTracking().SingleAsync(item => item.Id == session.UserId, ct);
        var context = session.Assurance >= AuthenticationAssuranceLevel.Mfa
            ? interaction.RequestedContextClass ?? SamlIdpMessages.MultiFactor
            : SamlIdpMessages.PasswordProtectedTransport;
        var content = new SamlAssertionContent(
            NameIdFor(provider, user, interaction.NameIdFormat), interaction.NameIdFormat, session.AuthenticatedAt, session.SessionId.ToString(), context,
            await AttributesAsync(provider, user, ct));
        var response = SamlIdpMessages.BuildResponse(
            _keys.EntityId, interaction.AssertionConsumerServiceUrl, interaction.RequestId, provider.EntityId, _clock.UtcNow,
            Math.Clamp(provider.AssertionLifetimeMinutes, 1, 60), content, null, certificate, provider.SignResponse,
            provider.EncryptAssertions ? Certificate(provider.EncryptionCertificate) : null);
        await _audit.LogAsync("SAML_ASSERTION_ISSUED", user.Id, applicationCode: provider.ApplicationSystem.Code, entityName: nameof(SamlServiceProvider), entityId: provider.Id.ToString(),
            metadata: new { provider.EntityId, requestId = interaction.RequestId, idpInitiated = interaction.RequestId is null, nameIdFormat = interaction.NameIdFormat, authnContext = context, sessionId = session.SessionId }, ct: ct);
        return Post(interaction.AssertionConsumerServiceUrl, response, interaction.RelayState);
    }

    private async Task<SamlPostMessage> DenyAsync(SamlServiceProvider provider, SamlInteraction interaction, Guid userId, SsoAccessDecision decision, CancellationToken ct)
    {
        await _audit.LogAsync(decision.Reason == "AccessPolicy" ? "ACCESS_POLICY_DENIED" : "SAML_SSO_DENIED", userId, applicationCode: provider.ApplicationSystem.Code,
            entityName: nameof(SamlServiceProvider), entityId: provider.Id.ToString(),
            metadata: new { reason = decision.Reason, rule = decision.Policy?.MatchedRuleName, ruleId = decision.Policy?.MatchedRuleId, flow = "saml_sso", provider.EntityId }, ct: ct);
        var certificate = _keys.SigningCertificate(out _)!;
        var response = SamlIdpMessages.BuildResponse(_keys.EntityId, interaction.AssertionConsumerServiceUrl, interaction.RequestId, provider.EntityId, _clock.UtcNow, 1,
            null, (SamlIdpMessages.Responder, SamlIdpMessages.RequestDenied, "The user may not sign in to this application."), certificate, true, null);
        return Post(interaction.AssertionConsumerServiceUrl, response, interaction.RelayState);
    }

    private async Task<SamlPostMessage> ErrorResponseAsync(SamlServiceProvider provider, SamlInboundRequest? request, string destination, string? relayState, string subStatus, string message, Guid? userId, CancellationToken ct)
    {
        await _audit.LogAsync("SAML_SSO_DENIED", userId, applicationCode: provider.ApplicationSystem.Code, entityName: nameof(SamlServiceProvider), entityId: provider.Id.ToString(),
            metadata: new { reason = subStatus[(subStatus.LastIndexOf(':') + 1)..], flow = "saml_sso", provider.EntityId }, ct: ct);
        var status = subStatus == SamlIdpMessages.InvalidNameIdPolicy ? SamlIdpMessages.Requester : SamlIdpMessages.Responder;
        var response = SamlIdpMessages.BuildResponse(_keys.EntityId, destination, request?.Id, provider.EntityId, _clock.UtcNow, 1, null, (status, subStatus, message), _keys.SigningCertificate(out _)!, true, null);
        return Post(destination, response, relayState);
    }

    private async Task<List<SamlAttribute>> AttributesAsync(SamlServiceProvider provider, ApplicationUser user, CancellationToken ct)
    {
        var attributes = new List<SamlAttribute>();
        Dictionary<string, JsonElement>? profile = null;
        foreach (var mapping in SamlAttributeMapping.Read(provider.AttributesJson))
        {
            IReadOnlyList<string> values = mapping.Source switch
            {
                "email" => [user.Email ?? string.Empty],
                "name" => [user.FullName],
                "userId" => [user.Id.ToString()],
                "roles" => [.. await _roles.GetRoleNamesForUserAsync(user.Id, provider.ApplicationSystemId, ct)],
                "permissions" => [.. await _roles.GetPermissionCodesForUserAsync(user.Id, provider.ApplicationSystemId, ct)],
                "groups" => await _db.UserGroupMemberships.AsNoTracking()
                    .Where(membership => membership.UserId == user.Id && membership.Group.IsActive)
                    .Select(membership => membership.Group.Name).OrderBy(name => name).ToListAsync(ct),
                _ when mapping.Source.StartsWith(SamlAttributeMapping.ProfilePrefix, StringComparison.Ordinal) =>
                    ProfileValues(profile ??= await ProfileAsync(user.Id, ct), mapping.Source[SamlAttributeMapping.ProfilePrefix.Length..]),
                _ => []
            };
            attributes.Add(new SamlAttribute(mapping.Name, values.Where(value => value.Length > 0).ToList()));
        }
        return attributes;
    }

    private async Task<Dictionary<string, JsonElement>> ProfileAsync(Guid userId, CancellationToken ct)
    {
        var profile = await _profiles.GetUserProfileAsync(userId, ct);
        return profile.IsSuccess
            ? profile.Data!.Attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.OrdinalIgnoreCase)
            : [];
    }

    private static IReadOnlyList<string> ProfileValues(Dictionary<string, JsonElement> profile, string key) =>
        !profile.TryGetValue(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? []
            : [value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()];

    /// <summary>
    /// The name identifier of the user for this provider: the email address, a pairwise opaque value
    /// (persistent: it cannot be correlated across providers) or the user id.
    /// </summary>
    internal static string NameIdFor(SamlServiceProvider provider, ApplicationUser user, string format) => format switch
    {
        SamlNameIdFormats.EmailAddress => user.Email ?? string.Empty,
        SamlNameIdFormats.Persistent => WebEncoders.Base64UrlEncode(HMACSHA256.HashData(Convert.FromBase64String(provider.NameIdSalt), user.Id.ToByteArray())),
        _ => user.Id.ToString()
    };

    /// <summary>The requested format when supported (unspecified leaves the provider's own); null when it cannot be honored.</summary>
    private static string? NameIdFormat(SamlServiceProvider provider, string? requested) =>
        requested is null || requested == SamlNameIdFormats.Unspecified ? provider.NameIdFormat
            : SamlNameIdFormats.Supported.Contains(requested) ? requested
            : null;

    private static string? LoginHint(SamlInboundRequest? request) =>
        request?.NameId is { } nameId && nameId.Contains('@') && nameId.Length <= 256 ? nameId : null;

    private async Task<SamlInteraction?> ReadInteractionAsync(string interactionId, string? browserBinding, CancellationToken ct)
    {
        var stored = await _state.GetAsync(InteractionPurpose, interactionId, ct);
        if (stored is null)
            return null;
        var interaction = JsonSerializer.Deserialize<SamlInteraction>(stored)!;
        return BindingMatches(interaction.BindingHash, browserBinding) ? interaction : null;
    }

    private Task<SamlServiceProvider?> ActiveProviderAsync(Guid id, CancellationToken ct) =>
        _db.SamlServiceProviders.AsNoTracking().Include(item => item.ApplicationSystem)
            .SingleOrDefaultAsync(item => item.Id == id && item.IsActive && item.ApplicationSystem.IsActive, ct);

    internal static IReadOnlyList<string> AssertionConsumerServices(SamlServiceProvider provider) =>
        JsonSerializer.Deserialize<string[]>(provider.AssertionConsumerServiceUrlsJson) ?? [];

    internal static X509Certificate2? Certificate(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;
        try
        {
            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return null;
        }
    }

    private static SamlPostMessage Post(string destination, string samlResponse, string? relayState)
    {
        var fields = new List<KeyValuePair<string, string>> { new("SAMLResponse", samlResponse) };
        if (!string.IsNullOrEmpty(relayState))
            fields.Add(new("RelayState", relayState));
        return new SamlPostMessage(destination, fields);
    }

    private static string Hash(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool BindingMatches(string storedHash, string? browserBinding) =>
        !string.IsNullOrEmpty(browserBinding) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(storedHash), Encoding.UTF8.GetBytes(Hash(browserBinding)));

    private static string? Bound(string? value, int length) => value is null || value.Length <= length ? value : value[..length];

    private sealed record SamlInteraction(
        Guid ServiceProviderId,
        Guid ApplicationSystemId,
        string? RequestId,
        string AssertionConsumerServiceUrl,
        string? RelayState,
        bool ForceAuthn,
        bool IsPassive,
        string NameIdFormat,
        AuthenticationAssuranceLevel? RequestedAssurance,
        string? RequestedContextClass,
        string? LoginHint,
        string BindingHash,
        DateTime CreatedAt);

    private sealed record PendingResponse(string Destination, Dictionary<string, string> Fields, string BindingHash);

    private sealed record PostedMessage(string SamlMessage, string? RelayState);
}

/// <summary>An attribute statement entry: its SAML name and where its values come from.</summary>
public sealed record SamlAttributeMapping(string Name, string Source)
{
    public const string ProfilePrefix = "profile:";
    public static readonly IReadOnlyList<string> Sources = ["email", "name", "userId", "roles", "permissions", "groups"];

    public static IReadOnlyList<SamlAttributeMapping> Read(string json) =>
        JsonSerializer.Deserialize<List<SamlAttributeMapping>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
}
