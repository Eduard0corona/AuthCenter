using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Contracts.Responses.Federation;
using AuthCenter.Contracts.Responses.OAuth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services;

public class OAuthAuthorizationService : IOAuthAuthorizationService
{
    private const string SessionPrefix = "oauth_session";
    private const string ResponsePrefix = "oauth_response";
    private const int AuthorizationLifetimeMinutes = 10;
    private const int PendingResponseSeconds = 120;
    private const string PromptNone = "none";
    private const string PromptLogin = "login";
    private const string PromptConsent = "consent";
    private const string PromptSelectAccount = "select_account";
    private static readonly string[] SupportedPromptValues = [PromptNone, PromptLogin, PromptConsent, PromptSelectAccount];
    private static readonly Regex PkceChallengePattern = new("^[A-Za-z0-9_-]{43}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PkceVerifierPattern = new("^[A-Za-z0-9\\-._~]{43,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AuthCenterDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly ITransientStateStore _transientState;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IUserAccessService _userAccessService;
    private readonly IRoleService _roleService;
    private readonly IAccessPolicyService _accessPolicies;
    private readonly IAuthenticationRiskService _authenticationRisk;
    private readonly JwtSettings _jwtSettings;

    public OAuthAuthorizationService(
        AuthCenterDbContext db,
        ITokenService tokenService,
        ITransientStateStore transientState,
        IDateTimeProvider dateTimeProvider,
        UserManager<ApplicationUser> userManager,
        IUserAccessService userAccessService,
        IRoleService roleService,
        IAccessPolicyService accessPolicies,
        IAuthenticationRiskService authenticationRisk,
        IOptions<JwtSettings> jwtSettings)
    {
        _db = db;
        _tokenService = tokenService;
        _transientState = transientState;
        _dateTimeProvider = dateTimeProvider;
        _userManager = userManager;
        _userAccessService = userAccessService;
        _roleService = roleService;
        _accessPolicies = accessPolicies;
        _authenticationRisk = authenticationRisk;
        _jwtSettings = jwtSettings.Value;
    }

    public async Task<OperationResult<AuthorizationEndpointResult>> InitiateAuthorizationAsync(
        AuthorizeRequest request,
        AuthorizationCaller caller,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return OperationResult<AuthorizationEndpointResult>.Failure("INVALID_CLIENT", "client_id is required.");

        var client = await FindActiveClientAsync(request.ClientId, ct);
        if (client is null)
            return OperationResult<AuthorizationEndpointResult>.Failure("INVALID_CLIENT", "Unknown, inactive, or unlinked OAuth client.");

        var allowedRedirectUris = DeserializeValues(client.RedirectUrisJson);
        if (string.IsNullOrWhiteSpace(request.RedirectUri) ||
            !allowedRedirectUris.Contains(request.RedirectUri, StringComparer.Ordinal))
        {
            return OperationResult<AuthorizationEndpointResult>.Failure("INVALID_REDIRECT_URI", "The redirect_uri is not registered for this client.");
        }

        // From here on the redirect URI is trusted, so protocol errors go back to the client.
        var responseMode = string.IsNullOrWhiteSpace(request.ResponseMode) ? AuthorizationResponse.Query : request.ResponseMode;
        if (responseMode is not (AuthorizationResponse.Query or AuthorizationResponse.FormPost) ||
            (responseMode == AuthorizationResponse.FormPost && !IsWebUri(request.RedirectUri)))
        {
            return Answer(ErrorResponse(request.RedirectUri, request.State, AuthorizationResponse.Query, "invalid_request", "response_mode must be query, or form_post for an HTTPS redirect URI."));
        }

        AuthorizationEndpointResult Error(string error, string description) =>
            new() { Response = ErrorResponse(request.RedirectUri, request.State, responseMode, error, description) };

        if (!string.Equals(request.ResponseType, "code", StringComparison.Ordinal))
            return OperationResult<AuthorizationEndpointResult>.Success(Error("unsupported_response_type", "Only response_type=code is supported."));
        if (!string.IsNullOrWhiteSpace(request.Request))
            return OperationResult<AuthorizationEndpointResult>.Success(Error("request_not_supported", "Request objects are not supported."));
        if (!string.IsNullOrWhiteSpace(request.RequestUri))
            return OperationResult<AuthorizationEndpointResult>.Success(Error("request_uri_not_supported", "request_uri is not supported."));

        var grants = DeserializeValues(client.GrantTypesJson);
        if (!grants.Contains("authorization_code", StringComparer.Ordinal))
            return OperationResult<AuthorizationEndpointResult>.Success(Error("unauthorized_client", "This client cannot use authorization_code."));

        var requestedScopes = ParseScopes(request.Scope);
        var allowedScopes = DeserializeValues(client.AllowedScopesJson);
        if (requestedScopes.Count == 0 || requestedScopes.Any(scope => !allowedScopes.Contains(scope, StringComparer.Ordinal)))
            return OperationResult<AuthorizationEndpointResult>.Success(Error("invalid_scope", "One or more requested scopes are not allowed."));

        if (requestedScopes.Contains(DomainConstants.OAuthScopes.OfflineAccess) &&
            !grants.Contains("refresh_token", StringComparer.Ordinal))
        {
            return OperationResult<AuthorizationEndpointResult>.Success(Error("invalid_scope", "offline_access requires the refresh_token grant."));
        }

        var resolution = await ResolveResourcesAsync(requestedScopes, request.Resources, ct);
        if (resolution.Error is { } resourceError)
            return OperationResult<AuthorizationEndpointResult>.Success(Error(resourceError.Code, resourceError.Description));

        if (string.IsNullOrWhiteSpace(request.State))
            return OperationResult<AuthorizationEndpointResult>.Success(new AuthorizationEndpointResult
            {
                Response = ErrorResponse(request.RedirectUri, null, responseMode, "invalid_request", "state is required.")
            });

        if (requestedScopes.Contains(DomainConstants.OAuthScopes.OpenId) && string.IsNullOrWhiteSpace(request.Nonce))
            return OperationResult<AuthorizationEndpointResult>.Success(Error("invalid_request", "nonce is required for OpenID Connect requests."));

        if (client.RequirePkce || client.ClientType == OAuthClientType.Public)
        {
            if (string.IsNullOrWhiteSpace(request.CodeChallenge) || !PkceChallengePattern.IsMatch(request.CodeChallenge))
                return OperationResult<AuthorizationEndpointResult>.Success(Error("invalid_request", "A valid S256 PKCE code_challenge is required."));

            if (!string.Equals(request.CodeChallengeMethod, "S256", StringComparison.Ordinal))
                return OperationResult<AuthorizationEndpointResult>.Success(Error("invalid_request", "code_challenge_method must be S256."));
        }
        else if (!string.IsNullOrWhiteSpace(request.CodeChallenge))
        {
            if (!PkceChallengePattern.IsMatch(request.CodeChallenge) ||
                !string.Equals(request.CodeChallengeMethod, "S256", StringComparison.Ordinal))
            {
                return OperationResult<AuthorizationEndpointResult>.Success(Error("invalid_request", "PKCE must use a valid S256 code_challenge."));
            }
        }

        var prompt = ParseScopes(request.Prompt);
        if (prompt.Any(value => !SupportedPromptValues.Contains(value, StringComparer.Ordinal)) ||
            (prompt.Contains(PromptNone) && prompt.Count > 1))
        {
            return OperationResult<AuthorizationEndpointResult>.Success(Error("invalid_request", "prompt must be none, login, consent or select_account, and none cannot be combined."));
        }

        int? maxAge = null;
        if (!string.IsNullOrWhiteSpace(request.MaxAge))
        {
            if (!int.TryParse(request.MaxAge, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedMaxAge))
                return OperationResult<AuthorizationEndpointResult>.Success(Error("invalid_request", "max_age must be a non-negative integer."));
            maxAge = parsedMaxAge;
        }

        if (request.LoginHint is { Length: > 256 })
            return OperationResult<AuthorizationEndpointResult>.Success(Error("invalid_request", "login_hint is too long."));

        // idp and domain_hint only steer the hosted login towards an upstream provider; the
        // provider must belong to the client's application.
        Guid? identityProviderId = null;
        if (!string.IsNullOrWhiteSpace(request.IdentityProvider))
        {
            if (!Guid.TryParse(request.IdentityProvider, out var providerId) ||
                !await _db.FederationProviders.AnyAsync(provider =>
                    provider.Id == providerId && provider.IsActive && provider.ApplicationSystemId == client.ApplicationSystemId, ct))
            {
                return OperationResult<AuthorizationEndpointResult>.Success(Error("invalid_request", "idp is not an active federation provider of this application."));
            }
            identityProviderId = providerId;
        }

        string? domainHint = null;
        if (!string.IsNullOrWhiteSpace(request.DomainHint))
        {
            domainHint = FederationDomains.Normalize(request.DomainHint);
            if (domainHint is null)
                return OperationResult<AuthorizationEndpointResult>.Success(Error("invalid_request", "domain_hint must be a domain name."));
        }

        string? hintSubject = null;
        if (!string.IsNullOrWhiteSpace(request.IdTokenHint))
        {
            hintSubject = _tokenService.ReadIdTokenHintSubject(request.IdTokenHint, client.ClientId);
            if (hintSubject is null)
                return OperationResult<AuthorizationEndpointResult>.Success(Error("invalid_request", "id_token_hint was not issued by this server to this client."));
        }

        var now = _dateTimeProvider.UtcNow;
        var session = new OAuthAuthorizationSession
        {
            ApplicationSystemId = client.ApplicationSystemId,
            ApplicationCode = client.ApplicationSystem.Code,
            ClientId = client.ClientId,
            RedirectUri = request.RedirectUri,
            Scopes = requestedScopes,
            Resources = resolution.Resources.Select(resource => resource.Identifier).ToList(),
            State = request.State,
            CodeChallenge = request.CodeChallenge,
            CodeChallengeMethod = request.CodeChallengeMethod,
            Nonce = request.Nonce,
            CreatedAt = now,
            Prompt = prompt,
            MaxAge = maxAge,
            LoginHint = string.IsNullOrWhiteSpace(request.LoginHint) ? null : request.LoginHint.Trim(),
            IdentityProviderId = identityProviderId,
            DomainHint = domainHint,
            IdTokenHintSubject = hintSubject,
            AcrValues = ParseScopes(request.AcrValues),
            ResponseMode = responseMode,
            BrowserBindingHash = HashBinding(caller.BrowserBinding)
        };

        // Single sign-on: a valid session answers the client directly unless the request needs a
        // fresh sign-in, a different account, a stronger sign-in for this application, or consent
        // the user has not given yet. The client's application decides with its own access policy.
        var existing = await ResolveSessionAsync(caller, ct);
        var sessionUsable = existing is not null &&
            !RequiresReauthentication(session, existing, now) &&
            (hintSubject is null || string.Equals(hintSubject, existing.UserId.ToString(), StringComparison.OrdinalIgnoreCase));
        var decision = sessionUsable ? await EvaluateTargetAsync(session, client, existing!, caller, ct) : null;
        if (decision?.Outcome == TargetOutcome.Denied && !prompt.Contains(PromptLogin) && !prompt.Contains(PromptSelectAccount))
            return Answer(await DenyAsync(session, client, existing!.UserId, decision, ct));

        if (prompt.Contains(PromptNone))
        {
            // A step-up needs the user, so a silent request cannot complete it.
            if (decision?.Outcome != TargetOutcome.Allowed)
                return OperationResult<AuthorizationEndpointResult>.Success(Error("login_required", "The user must sign in."));
            if (await RequiresConsentAsync(session, client, existing!.UserId, ct))
                return OperationResult<AuthorizationEndpointResult>.Success(Error("consent_required", "The user must grant consent."));
            return Answer(await IssueAuthorizationCodeAsync(session, client, existing, recordConsent: false, ct));
        }

        if (decision?.Outcome == TargetOutcome.Allowed &&
            !prompt.Contains(PromptLogin) &&
            !prompt.Contains(PromptSelectAccount) &&
            !await RequiresConsentAsync(session, client, existing!.UserId, ct))
        {
            return Answer(await IssueAuthorizationCodeAsync(session, client, existing!, recordConsent: false, ct));
        }

        var interactionId = Guid.NewGuid().ToString("N");
        await _transientState.SetAsync(
            SessionPrefix,
            interactionId,
            JsonSerializer.Serialize(session),
            now.AddMinutes(AuthorizationLifetimeMinutes),
            ct);

        var separator = client.LoginUrl.Contains('?') ? '&' : '?';
        return OperationResult<AuthorizationEndpointResult>.Success(new AuthorizationEndpointResult
        {
            LoginUrl = $"{client.LoginUrl}{separator}interaction_id={interactionId}&state={Uri.EscapeDataString(request.State)}"
        });
    }

    public async Task<OperationResult<OAuthInteractionContextResponse>> GetInteractionContextAsync(
        string interactionId,
        string? browserBinding,
        CancellationToken ct = default)
    {
        var session = await ReadInteractionAsync(interactionId, ct);
        if (session is null || !BindingMatches(session.BrowserBindingHash, browserBinding))
            return OperationResult<OAuthInteractionContextResponse>.Failure("INVALID_INTERACTION", "Interaction not found or expired.");

        var client = await FindActiveClientAsync(session.ClientId, ct);
        if (client is null || client.ApplicationSystemId != session.ApplicationSystemId)
            return OperationResult<OAuthInteractionContextResponse>.Failure("INVALID_CLIENT", "OAuth client is no longer active.");

        var settings = await _db.ApplicationRegistrationSettings.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ApplicationSystemId == client.ApplicationSystemId, ct);
        var providers = await _db.FederationProviders.AsNoTracking()
            .Where(provider => provider.ApplicationSystemId == client.ApplicationSystemId && provider.IsActive)
            .Select(provider => new { provider.Id, provider.Name, provider.Protocol })
            .ToListAsync(ct);
        var identityProvider = providers.FirstOrDefault(provider => provider.Id == session.IdentityProviderId);
        return OperationResult<OAuthInteractionContextResponse>.Success(new OAuthInteractionContextResponse
        {
            ApplicationCode = client.ApplicationSystem.Code,
            ApplicationName = client.ApplicationSystem.Name,
            ClientDisplayName = client.DisplayName,
            LoginHint = session.LoginHint,
            RequiresFreshLogin = session.Prompt.Contains(PromptLogin) || session.Prompt.Contains(PromptSelectAccount),
            AllowPasswordLogin = settings?.AllowPasswordLogin ?? true,
            AllowMagicLink = settings?.AllowMagicLink ?? false,
            FederationAvailable = providers.Count > 0,
            IdentityProvider = identityProvider is null ? null : new FederationProviderSummary
            {
                Id = identityProvider.Id,
                Name = identityProvider.Name,
                Protocol = identityProvider.Protocol.ToString()
            },
            DomainHint = session.DomainHint,
            ExpiresAt = DateTime.SpecifyKind(session.CreatedAt, DateTimeKind.Utc).AddMinutes(AuthorizationLifetimeMinutes)
        });
    }

    public async Task<InteractionTarget?> GetInteractionTargetAsync(string interactionId, string? browserBinding, CancellationToken ct = default)
    {
        var session = await ReadInteractionAsync(interactionId, ct);
        if (session is null || !BindingMatches(session.BrowserBindingHash, browserBinding))
            return null;
        var client = await FindActiveClientAsync(session.ClientId, ct);
        return client is null || client.ApplicationSystemId != session.ApplicationSystemId
            ? null
            : new InteractionTarget(
                client.ApplicationSystemId,
                client.ApplicationSystem.Code,
                session.LoginHint,
                session.Prompt.Contains(PromptLogin) || session.Prompt.Contains(PromptSelectAccount) || session.MaxAge is not null);
    }

    public async Task<OperationResult<OAuthInteractionResponse>> GetInteractionAsync(string interactionId, AuthorizationCaller caller, CancellationToken ct = default)
    {
        var session = await ReadInteractionAsync(interactionId, ct);
        if (session is null || caller.UserId is null)
            return OperationResult<OAuthInteractionResponse>.Failure("INVALID_INTERACTION", "Interaction not found or expired.");
        if (caller.RequiresBrowserBinding && !BindingMatches(session.BrowserBindingHash, caller.BrowserBinding))
            return OperationResult<OAuthInteractionResponse>.Failure("INTERACTION_BINDING_MISMATCH", "This sign-in request was started in a different browser.");

        var client = await FindActiveClientAsync(session.ClientId, ct);
        if (client is null || client.ApplicationSystemId != session.ApplicationSystemId)
            return OperationResult<OAuthInteractionResponse>.Failure("INVALID_CLIENT", "OAuth client is no longer active.");

        var existing = await ResolveSessionAsync(caller, ct);
        return OperationResult<OAuthInteractionResponse>.Success(new OAuthInteractionResponse
        {
            ClientId = client.ClientId,
            ClientDisplayName = client.DisplayName,
            ApplicationCode = client.ApplicationSystem.Code,
            ApplicationName = client.ApplicationSystem.Name,
            Scopes = session.Scopes,
            RequiresConsent = await RequiresConsentAsync(session, client, caller.UserId.Value, ct),
            RequiresReauthentication = existing is null || RequiresReauthentication(session, existing, _dateTimeProvider.UtcNow) ||
                (session.IdTokenHintSubject is not null && !string.Equals(session.IdTokenHintSubject, caller.UserId.Value.ToString(), StringComparison.OrdinalIgnoreCase)),
            ExpiresAt = DateTime.SpecifyKind(session.CreatedAt, DateTimeKind.Utc).AddMinutes(AuthorizationLifetimeMinutes)
        });
    }

    public async Task<OperationResult<AuthorizationResponse>> CompleteAuthorizationAsync(
        CompleteAuthorizationRequest request,
        AuthorizationCaller caller,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.InteractionId))
            return OperationResult<AuthorizationResponse>.Failure("INVALID_INTERACTION", "Interaction not found or expired. Start a new authorization request.");

        var session = await ReadInteractionAsync(request.InteractionId, ct);
        if (session is null)
            return OperationResult<AuthorizationResponse>.Failure("INVALID_INTERACTION", "Interaction not found or expired. Start a new authorization request.");
        if (caller.RequiresBrowserBinding && !BindingMatches(session.BrowserBindingHash, caller.BrowserBinding))
            return OperationResult<AuthorizationResponse>.Failure("INTERACTION_BINDING_MISMATCH", "This sign-in request was started in a different browser.");

        var client = await FindActiveClientAsync(session.ClientId, ct);
        if (client is null || client.ApplicationSystemId != session.ApplicationSystemId)
            return OperationResult<AuthorizationResponse>.Failure("INVALID_CLIENT", "OAuth client is no longer active.");

        // The interaction is only consumed once the current session satisfies it, so the hosted
        // login can ask for a fresh sign-in and complete the same request afterwards.
        var existing = await ResolveSessionAsync(caller, ct);
        if (existing is null ||
            RequiresReauthentication(session, existing, _dateTimeProvider.UtcNow) ||
            (session.IdTokenHintSubject is not null && !string.Equals(session.IdTokenHintSubject, existing.UserId.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            return OperationResult<AuthorizationResponse>.Failure("LOGIN_REQUIRED", "Sign in again to continue with this application.");
        }

        // A stronger sign-in keeps the interaction, so the hosted login can step up and complete
        // the same request; a denial is final and goes back to the client.
        var decision = await EvaluateTargetAsync(session, client, existing, caller, ct);
        if (decision.Outcome == TargetOutcome.StepUp)
            return OperationResult<AuthorizationResponse>.Failure("STEP_UP_REQUIRED", "This application requires a stronger sign-in.");

        var requiresConsent = await RequiresConsentAsync(session, client, existing.UserId, ct);
        if (await _transientState.TakeAsync(SessionPrefix, request.InteractionId, ct) is null)
            return OperationResult<AuthorizationResponse>.Failure("INVALID_INTERACTION", "Interaction not found or expired. Start a new authorization request.");

        if (decision.Outcome == TargetOutcome.Denied)
            return OperationResult<AuthorizationResponse>.Success(await DenyAsync(session, client, existing.UserId, decision, ct));

        if (requiresConsent && !request.Consent)
        {
            AddAudit("OAUTH_CONSENT_DENIED", existing.UserId, client, metadata: new { scopes = session.Scopes });
            await _db.SaveChangesAsync(ct);
            return OperationResult<AuthorizationResponse>.Success(ErrorResponse(session.RedirectUri, session.State, session.ResponseMode, "access_denied", "The resource owner denied the request."));
        }

        return OperationResult<AuthorizationResponse>.Success(
            await IssueAuthorizationCodeAsync(session, client, existing, recordConsent: request.Consent && !client.AutoConsent, ct));
    }

    public async Task<OperationResult<StepUpRequirement>> GetStepUpRequirementAsync(
        string interactionId,
        AuthorizationCaller caller,
        CancellationToken ct = default)
    {
        var session = await ReadInteractionAsync(interactionId, ct);
        if (session is null)
            return OperationResult<StepUpRequirement>.Failure("INVALID_INTERACTION", "Interaction not found or expired. Start a new authorization request.");
        if (caller.RequiresBrowserBinding && !BindingMatches(session.BrowserBindingHash, caller.BrowserBinding))
            return OperationResult<StepUpRequirement>.Failure("INTERACTION_BINDING_MISMATCH", "This sign-in request was started in a different browser.");

        var client = await FindActiveClientAsync(session.ClientId, ct);
        if (client is null || client.ApplicationSystemId != session.ApplicationSystemId)
            return OperationResult<StepUpRequirement>.Failure("INVALID_CLIENT", "OAuth client is no longer active.");

        var existing = await ResolveSessionAsync(caller, ct);
        if (existing is null ||
            RequiresReauthentication(session, existing, _dateTimeProvider.UtcNow) ||
            (session.IdTokenHintSubject is not null && !string.Equals(session.IdTokenHintSubject, existing.UserId.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            return OperationResult<StepUpRequirement>.Failure("LOGIN_REQUIRED", "Sign in again to continue with this application.");
        }

        var decision = await EvaluateTargetAsync(session, client, existing, caller, ct);
        if (decision.Outcome == TargetOutcome.Denied)
            return OperationResult<StepUpRequirement>.Failure("ACCESS_DENIED", "The application's access policy does not allow this sign-in.");

        return OperationResult<StepUpRequirement>.Success(new StepUpRequirement(
            client.ApplicationSystem.Code,
            decision.Outcome == TargetOutcome.StepUp ? decision.RequiredAssurance : null,
            existing.Methods.FirstOrDefault() ?? DomainConstants.AuthenticationMethods.Password));
    }

    private enum TargetOutcome { Allowed, StepUp, Denied }

    private sealed record TargetDecision(TargetOutcome Outcome, AuthenticationAssuranceLevel RequiredAssurance, AccessPolicyDecision? Policy, string Reason);

    /// <summary>
    /// Decides whether a single sign-on session may be used for the client's application now: the
    /// user's access, the application's published access policy (evaluated with this browser's
    /// address and risk), the application's MFA setting, the user's own MFA and the acr_values
    /// requested. A session below the required assurance can be stepped up; a denial cannot.
    /// </summary>
    private async Task<TargetDecision> EvaluateTargetAsync(
        OAuthAuthorizationSession request,
        OAuthClient client,
        SsoSession session,
        AuthorizationCaller caller,
        CancellationToken ct)
    {
        if (!await _userAccessService.HasActiveAccessAsync(session.UserId, client.ApplicationSystemId, ct))
            return new TargetDecision(TargetOutcome.Denied, session.Assurance, null, "NoApplicationAccess");

        var signals = await _authenticationRisk.AssessAndRecordAsync(session.UserId, caller.IpAddress, caller.UserAgent, ct: ct);
        var policy = await _accessPolicies.EvaluateAsync(new AccessPolicyEvaluationContext(
            session.UserId,
            client.ApplicationSystemId,
            caller.IpAddress,
            _dateTimeProvider.UtcNow,
            signals.RiskLevel,
            session.Assurance), ct);
        if (!policy.IsAllowed)
            return new TargetDecision(TargetOutcome.Denied, session.Assurance, policy, "AccessPolicy");

        var required = policy.RequiredAssuranceLevel;
        if (policy.RequireMfa && required < AuthenticationAssuranceLevel.Mfa)
            required = AuthenticationAssuranceLevel.Mfa;
        var applicationRequiresMfa = await _db.ApplicationRegistrationSettings.AsNoTracking()
            .AnyAsync(item => item.ApplicationSystemId == client.ApplicationSystemId && item.RequireMfa, ct);
        var userHasMfa = await _db.UserMfaCredentials.AsNoTracking()
            .AnyAsync(item => item.UserId == session.UserId && item.IsEnabled, ct);
        if ((applicationRequiresMfa || userHasMfa) && required < AuthenticationAssuranceLevel.Mfa)
            required = AuthenticationAssuranceLevel.Mfa;
        if (RequestedAssurance(request.AcrValues) is { } requested && requested > required)
            required = requested;

        return session.Assurance >= required
            ? new TargetDecision(TargetOutcome.Allowed, required, policy, "Allowed")
            : new TargetDecision(TargetOutcome.StepUp, required, policy, "StepUpRequired");
    }

    /// <summary>
    /// acr_values lists acceptable classes in order of preference, so the least demanding one the
    /// server supports is the minimum the client accepts. Unknown values are ignored.
    /// </summary>
    private static AuthenticationAssuranceLevel? RequestedAssurance(IEnumerable<string> acrValues)
    {
        var levels = acrValues
            .Select(AuthenticationContext.AssuranceFor)
            .Where(level => level.HasValue)
            .Select(level => level!.Value)
            .ToList();
        return levels.Count == 0 ? null : levels.Min();
    }

    private async Task<AuthorizationResponse> DenyAsync(
        OAuthAuthorizationSession session,
        OAuthClient client,
        Guid userId,
        TargetDecision decision,
        CancellationToken ct)
    {
        AddAudit(decision.Policy is null ? "OAUTH_ACCESS_DENIED" : "ACCESS_POLICY_DENIED", userId, client, metadata: new
        {
            reason = decision.Reason,
            rule = decision.Policy?.MatchedRuleName,
            ruleId = decision.Policy?.MatchedRuleId,
            flow = "oauth_authorize"
        });
        await _db.SaveChangesAsync(ct);
        return ErrorResponse(session.RedirectUri, session.State, session.ResponseMode, "access_denied",
            decision.Policy is null
                ? "The user does not have access to this application."
                : "The application's access policy does not allow this sign-in.");
    }

    public async Task<string> StorePendingResponseAsync(AuthorizationResponse response, string? browserBinding, CancellationToken ct = default)
    {
        var id = GenerateCode();
        var pending = new PendingAuthorizationResponse(response.RedirectUri, response.ResponseMode, response.Parameters.ToDictionary(item => item.Key, item => item.Value), HashBinding(browserBinding));
        await _transientState.SetAsync(ResponsePrefix, id, JsonSerializer.Serialize(pending), _dateTimeProvider.UtcNow.AddSeconds(PendingResponseSeconds), ct);
        return id;
    }

    public async Task<AuthorizationResponse?> TakePendingResponseAsync(string responseId, string? browserBinding, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(responseId))
            return null;
        var stored = await _transientState.TakeAsync(ResponsePrefix, responseId, ct);
        if (string.IsNullOrWhiteSpace(stored))
            return null;
        PendingAuthorizationResponse? pending;
        try { pending = JsonSerializer.Deserialize<PendingAuthorizationResponse>(stored); }
        catch (JsonException) { return null; }
        if (pending is null || !BindingMatches(pending.BindingHash, browserBinding))
            return null;
        return new AuthorizationResponse { RedirectUri = pending.RedirectUri, ResponseMode = pending.ResponseMode, Parameters = pending.Parameters.ToList() };
    }

    private sealed record PendingAuthorizationResponse(string RedirectUri, string ResponseMode, Dictionary<string, string> Parameters, string? BindingHash);

    private sealed record SsoSession(Guid UserId, Guid SessionId, DateTime AuthenticatedAt, IReadOnlyList<string> Methods, AuthenticationAssuranceLevel Assurance);

    private async Task<SsoSession?> ResolveSessionAsync(AuthorizationCaller caller, CancellationToken ct)
    {
        if (caller.UserId is not { } userId || caller.SessionId is not { } sessionId)
            return null;
        var now = _dateTimeProvider.UtcNow;
        var row = await _db.RefreshTokens.AsNoTracking()
            .Where(token => token.Id == sessionId && token.UserId == userId && token.OAuthClientId == null &&
                token.RevokedAt == null && token.ExpiresAt > now && token.User.IsActive)
            .Select(token => new { token.AuthenticatedAt, token.CreatedAt, token.AuthenticationMethods, token.AssuranceLevel })
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return null;
        var methods = AuthenticationContext.ParseMethods(row.AuthenticationMethods);
        return new SsoSession(
            userId,
            sessionId,
            row.AuthenticatedAt ?? row.CreatedAt,
            methods.Count > 0 ? methods : [DomainConstants.AuthenticationMethods.Password],
            row.AssuranceLevel is { } level && Enum.IsDefined(typeof(AuthenticationAssuranceLevel), level)
                ? (AuthenticationAssuranceLevel)level
                : AuthenticationAssuranceLevel.Password);
    }

    private static bool RequiresReauthentication(OAuthAuthorizationSession session, SsoSession existing, DateTime now)
    {
        // A sign-in performed after this request started satisfies prompt=login and any max_age,
        // including max_age=0, however long the user then spends on the consent screen.
        if (existing.AuthenticatedAt >= session.CreatedAt)
            return false;

        return session.Prompt.Contains(PromptLogin) ||
            session.Prompt.Contains(PromptSelectAccount) ||
            (session.MaxAge is { } maxAge && (now - existing.AuthenticatedAt).TotalSeconds > maxAge);
    }

    private async Task<bool> RequiresConsentAsync(OAuthAuthorizationSession session, OAuthClient client, Guid userId, CancellationToken ct) =>
        session.Prompt.Contains(PromptConsent) ||
        (!client.AutoConsent && !await HasConsentAsync(userId, client.Id, session.Scopes, ct));

    private async Task<AuthorizationResponse> IssueAuthorizationCodeAsync(
        OAuthAuthorizationSession session,
        OAuthClient client,
        SsoSession existing,
        bool recordConsent,
        CancellationToken ct)
    {
        var user = await _userManager.FindByIdAsync(existing.UserId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return ErrorResponse(session.RedirectUri, session.State, session.ResponseMode, "access_denied", "The authenticated account is inactive.");

        if (!await _userAccessService.HasActiveAccessAsync(existing.UserId, client.ApplicationSystemId, ct))
        {
            AddAudit("OAUTH_ACCESS_DENIED", existing.UserId, client, metadata: new { reason = "NoApplicationAccess" });
            await _db.SaveChangesAsync(ct);
            return ErrorResponse(session.RedirectUri, session.State, session.ResponseMode, "access_denied", "The user does not have access to this application.");
        }

        // The APIs may belong to other applications: the user needs access to each of them too.
        var resolution = await ResolveResourcesAsync(session.Scopes, session.Resources, ct);
        if (resolution.Error is { } resourceError)
            return ErrorResponse(session.RedirectUri, session.State, session.ResponseMode, resourceError.Code, resourceError.Description);
        foreach (var applicationId in resolution.Resources.Select(resource => resource.ApplicationSystemId).Distinct().Where(id => id != client.ApplicationSystemId))
        {
            if (!await _userAccessService.HasActiveAccessAsync(existing.UserId, applicationId, ct))
            {
                AddAudit("OAUTH_ACCESS_DENIED", existing.UserId, client, metadata: new { reason = "NoApiApplicationAccess", applicationId });
                await _db.SaveChangesAsync(ct);
                return ErrorResponse(session.RedirectUri, session.State, session.ResponseMode, "access_denied", "The user does not have access to a requested API.");
            }
        }

        var rawCode = GenerateCode();
        var now = _dateTimeProvider.UtcNow;
        if (recordConsent)
            await UpsertConsentAsync(existing.UserId, client.Id, session.Scopes, now, ct);
        _db.OAuthAuthorizationCodes.Add(new OAuthAuthorizationCode
        {
            Id = Guid.NewGuid(),
            CodeHash = HashCode(rawCode),
            OAuthClientId = client.Id,
            UserId = existing.UserId,
            RedirectUri = session.RedirectUri,
            ScopesJson = JsonSerializer.Serialize(session.Scopes),
            ResourcesJson = JsonSerializer.Serialize(resolution.Resources.Select(resource => resource.Identifier)),
            CodeChallenge = session.CodeChallenge,
            CodeChallengeMethod = session.CodeChallengeMethod,
            Nonce = session.Nonce,
            ExpiresAt = now.AddMinutes(AuthorizationLifetimeMinutes),
            IsUsed = false,
            CreatedAt = now,
            SessionId = existing.SessionId,
            AuthenticatedAt = existing.AuthenticatedAt,
            AuthenticationMethods = string.Join(' ', existing.Methods),
            AssuranceLevel = (int)existing.Assurance
        });
        AddAudit("OAUTH_AUTHORIZATION_GRANTED", existing.UserId, client, metadata: new { scopes = session.Scopes, sessionId = existing.SessionId });
        await _db.SaveChangesAsync(ct);
        await RecordSessionClientAsync(existing, client, now, ct);

        return SuccessResponse(session.RedirectUri, session.State, session.ResponseMode, rawCode);
    }

    /// <summary>
    /// Remembers that the client received tokens through this session, so ending the session can
    /// send it a back-channel logout even after the authorization code is purged.
    /// </summary>
    private async Task RecordSessionClientAsync(SsoSession existing, OAuthClient client, DateTime now, CancellationToken ct)
    {
        var participation = await _db.SingleSignOnSessionClients
            .FirstOrDefaultAsync(item => item.SessionId == existing.SessionId && item.OAuthClientId == client.Id, ct);
        if (participation is null)
        {
            participation = new SingleSignOnSessionClient
            {
                SessionId = existing.SessionId,
                OAuthClientId = client.Id,
                UserId = existing.UserId,
                CreatedAt = now,
                LastIssuedAt = now
            };
            _db.SingleSignOnSessionClients.Add(participation);
        }
        else
        {
            participation.LastIssuedAt = now;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent authorization for the same session and client recorded it first.
            _db.Entry(participation).State = EntityState.Detached;
        }
    }

    private sealed record ResourceGrant(Guid Id, string Identifier, Guid ApplicationSystemId, string ApplicationCode, IReadOnlyList<string> Scopes);

    private sealed record ProtocolError(string Code, string Description);

    private sealed record ResourceResolution(IReadOnlyList<ResourceGrant> Resources, ProtocolError? Error);

    /// <summary>
    /// Maps the requested API scopes to their APIs (RFC 8707). Every API scope must exist on an
    /// active API; resource indicators, when sent, must name exactly the APIs of those scopes.
    /// </summary>
    private async Task<ResourceResolution> ResolveResourcesAsync(IEnumerable<string> scopes, IEnumerable<string> requestedResources, CancellationToken ct)
    {
        var apiScopeNames = scopes.Where(scope => !DomainConstants.OAuthScopes.All.Contains(scope, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        var resources = requestedResources.Where(resource => !string.IsNullOrWhiteSpace(resource)).Distinct(StringComparer.Ordinal).ToList();
        if (resources.Any(resource => !Uri.TryCreate(resource, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Fragment)))
            return new ResourceResolution([], new ProtocolError("invalid_target", "Resource indicators must be absolute URIs without a fragment."));
        if (apiScopeNames.Count == 0)
        {
            return resources.Count == 0
                ? new ResourceResolution([], null)
                : new ResourceResolution([], new ProtocolError("invalid_target", "A resource was requested without any of its scopes."));
        }

        var apiScopes = await _db.ApiScopes.AsNoTracking()
            .Where(scope => apiScopeNames.Contains(scope.Name) && scope.ApiResource.IsActive && scope.ApiResource.ApplicationSystem.IsActive)
            .Select(scope => new { scope.Name, scope.ApiResource.Id, scope.ApiResource.Identifier, scope.ApiResource.ApplicationSystemId, ApplicationCode = scope.ApiResource.ApplicationSystem.Code })
            .ToListAsync(ct);
        if (apiScopes.Count != apiScopeNames.Count)
            return new ResourceResolution([], new ProtocolError("invalid_scope", "One or more requested API scopes are not available."));

        var grants = apiScopes
            .GroupBy(scope => scope.Id)
            .Select(group => new ResourceGrant(group.Key, group.First().Identifier, group.First().ApplicationSystemId, group.First().ApplicationCode, group.Select(scope => scope.Name).ToList()))
            .ToList();
        if (resources.Count > 0 &&
            (resources.Any(resource => grants.All(grant => grant.Identifier != resource)) ||
             grants.Any(grant => !resources.Contains(grant.Identifier, StringComparer.Ordinal))))
        {
            return new ResourceResolution([], new ProtocolError("invalid_target", "Each resource must be an API whose scopes were requested, and each requested API scope must belong to a requested resource."));
        }

        return new ResourceResolution(grants, null);
    }

    /// <summary>
    /// Chooses the API a token is issued for. One token has one API audience (RFC 8707 section
    /// 2.2): with several granted APIs the client names one with the resource parameter.
    /// </summary>
    private static (ResourceGrant? Resource, ProtocolError? Error) SelectResource(IReadOnlyList<ResourceGrant> granted, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var match = granted.FirstOrDefault(grant => grant.Identifier == requested);
            return match is null
                ? (null, new ProtocolError("invalid_target", "The resource was not part of this grant."))
                : (match, null);
        }

        return granted.Count switch
        {
            0 => (null, null),
            1 => (granted[0], null),
            _ => (null, new ProtocolError("invalid_target", "This grant covers several APIs; name one with the resource parameter."))
        };
    }

    /// <summary>The scopes that apply to a token: OpenID Connect scopes plus the chosen API's scopes.</summary>
    private static List<string> ScopesFor(IEnumerable<string> granted, ResourceGrant? resource) =>
        granted.Where(scope => DomainConstants.OAuthScopes.All.Contains(scope, StringComparer.Ordinal) ||
                (resource is not null && resource.Scopes.Contains(scope, StringComparer.Ordinal)))
            .ToList();

    /// <summary>
    /// The token's audiences: the chosen API (plus UserInfo when openid was granted) or, without an
    /// API, the client itself.
    /// </summary>
    private static List<string> AudiencesFor(string clientId, ResourceGrant? resource, IReadOnlyCollection<string> scopes)
    {
        if (resource is null)
            return [clientId];
        return scopes.Contains(DomainConstants.OAuthScopes.OpenId)
            ? [resource.Identifier, DomainConstants.OAuthAudiences.UserInfo]
            : [resource.Identifier];
    }

    private sealed record TokenClaims(string ApplicationCode, IList<string> Roles, IList<string> Permissions);

    /// <summary>
    /// Roles and permissions come from the application that owns the token's API (or the client's
    /// application without one); the user must still have access to that application.
    /// </summary>
    private async Task<TokenClaims?> TokenClaimsForAsync(Guid userId, OAuthClient client, ResourceGrant? resource, CancellationToken ct)
    {
        var applicationId = resource?.ApplicationSystemId ?? client.ApplicationSystemId;
        if (applicationId != client.ApplicationSystemId && !await _userAccessService.HasActiveAccessAsync(userId, applicationId, ct))
            return null;
        return new TokenClaims(
            resource?.ApplicationCode ?? client.ApplicationSystem.Code,
            await _roleService.GetRoleNamesForUserAsync(userId, applicationId, ct),
            await _roleService.GetPermissionCodesForUserAsync(userId, applicationId, ct));
    }

    private async Task<OAuthAuthorizationSession?> ReadInteractionAsync(string interactionId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(interactionId)
            ? null
            : DeserializeSession(await _transientState.GetAsync(SessionPrefix, interactionId, ct));

    private static OperationResult<AuthorizationEndpointResult> Answer(AuthorizationResponse response) =>
        OperationResult<AuthorizationEndpointResult>.Success(new AuthorizationEndpointResult { Response = response });

    private static string? HashBinding(string? binding) =>
        string.IsNullOrWhiteSpace(binding) ? null : Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(binding)));

    // An interaction created without a browser binding (for example by a server-side client) has
    // nothing to compare; one created by a browser can only be continued by that browser.
    private static bool BindingMatches(string? storedHash, string? binding) =>
        storedHash is null || (HashBinding(binding) is { } presented && FixedTimeEquals(storedHash, presented));

    private static bool IsWebUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public async Task<IReadOnlyList<OAuthConsentGrantDto>> GetConsentGrantsAsync(Guid userId, CancellationToken ct = default)
    {
        var grants = await _db.OAuthConsentGrants
            .AsNoTracking()
            .Where(item => item.UserId == userId && item.OAuthClient.IsActive && item.OAuthClient.ApplicationSystem.IsActive)
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => new
            {
                item.Id,
                item.OAuthClient.ClientId,
                ClientDisplayName = item.OAuthClient.DisplayName,
                ApplicationCode = item.OAuthClient.ApplicationSystem.Code,
                ApplicationName = item.OAuthClient.ApplicationSystem.Name,
                item.ScopesJson,
                item.GrantedAt,
                item.UpdatedAt
            })
            .ToListAsync(ct);
        return grants.Select(item => new OAuthConsentGrantDto
        {
            Id = item.Id,
            ClientId = item.ClientId,
            ClientDisplayName = item.ClientDisplayName,
            ApplicationCode = item.ApplicationCode,
            ApplicationName = item.ApplicationName,
            Scopes = DeserializeValues(item.ScopesJson),
            GrantedAt = item.GrantedAt,
            UpdatedAt = item.UpdatedAt
        }).ToList();
    }

    public async Task<OperationResult> RevokeConsentGrantAsync(Guid userId, Guid grantId, CancellationToken ct = default)
    {
        var grant = await _db.OAuthConsentGrants
            .Include(item => item.OAuthClient)
            .ThenInclude(item => item.ApplicationSystem)
            .FirstOrDefaultAsync(item => item.Id == grantId && item.UserId == userId, ct);
        if (grant is null)
            return OperationResult.Failure("CONSENT_NOT_FOUND", "Consent grant was not found.");

        var now = _dateTimeProvider.UtcNow;
        var clientId = grant.OAuthClient.ClientId;
        _db.OAuthConsentGrants.Remove(grant);
        var tokens = await _db.RefreshTokens
            .Where(item => item.UserId == userId && item.OAuthClientId == clientId && item.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in tokens)
            token.RevokedAt = now;
        AddAudit("OAUTH_CONSENT_REVOKED", userId, grant.OAuthClient, metadata: new { grantId });
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult<OAuthTokenResponse>> ExchangeCodeAsync(OAuthTokenRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT", "client_id is required.");

        var client = await FindActiveClientAsync(request.ClientId, ct);
        if (client is null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT", "Unknown, inactive, or unlinked OAuth client.");

        var grantTypes = DeserializeValues(client.GrantTypesJson);
        if (!grantTypes.Contains("authorization_code", StringComparer.Ordinal))
            return OperationResult<OAuthTokenResponse>.Failure("UNAUTHORIZED_CLIENT", "This client is not authorized for authorization_code grant.");

        var clientAuthError = ValidateClientAuthentication(client, request.ClientSecret);
        if (clientAuthError is not null)
            return OperationResult<OAuthTokenResponse>.Failure(clientAuthError.Value.Code, clientAuthError.Value.Message);

        if (string.IsNullOrWhiteSpace(request.Code))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "code is required.");

        var authCode = await _db.OAuthAuthorizationCodes
            .FirstOrDefaultAsync(c => c.CodeHash == HashCode(request.Code), ct);

        if (authCode is null || authCode.OAuthClientId != client.Id)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "Authorization code is invalid.");
        if (authCode.IsUsed)
            return OperationResult<OAuthTokenResponse>.Failure("CODE_ALREADY_USED", "Authorization code has already been used.");
        if (authCode.ExpiresAt <= _dateTimeProvider.UtcNow)
            return OperationResult<OAuthTokenResponse>.Failure("CODE_EXPIRED", "Authorization code has expired.");
        if (!string.Equals(authCode.RedirectUri, request.RedirectUri, StringComparison.Ordinal))
            return OperationResult<OAuthTokenResponse>.Failure("REDIRECT_URI_MISMATCH", "redirect_uri does not match the authorization request.");

        if (!string.IsNullOrWhiteSpace(authCode.CodeChallenge))
        {
            if (string.IsNullOrWhiteSpace(request.CodeVerifier) || !PkceVerifierPattern.IsMatch(request.CodeVerifier))
                return OperationResult<OAuthTokenResponse>.Failure("CODE_VERIFIER_REQUIRED", "A valid code_verifier is required for this authorization code.");

            var computedChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier)));
            if (!FixedTimeEquals(computedChallenge, authCode.CodeChallenge))
                return OperationResult<OAuthTokenResponse>.Failure("INVALID_CODE_VERIFIER", "code_verifier does not match the code_challenge.");
        }
        else if (!string.IsNullOrWhiteSpace(request.CodeVerifier))
        {
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "code_verifier was not expected for this authorization code.");
        }

        var user = await _userManager.FindByIdAsync(authCode.UserId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "User account is inactive.");
        if (!await _userAccessService.HasActiveAccessAsync(user.Id, client.ApplicationSystemId, ct))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "User no longer has access to this application.");

        var scopes = DeserializeValues(authCode.ScopesJson);
        var granted = await ResolveResourcesAsync(scopes, DeserializeValues(authCode.ResourcesJson), ct);
        if (granted.Error is not null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "An API of this authorization is no longer available.");
        var (resource, targetError) = SelectResource(granted.Resources, request.Resource);
        if (targetError is not null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_TARGET", targetError.Description);
        var claims = await TokenClaimsForAsync(user.Id, client, resource, ct);
        if (claims is null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "User no longer has access to the requested API.");

        authCode.IsUsed = true;
        var tokenScopes = ScopesFor(scopes, resource);
        var authentication = ToTokenAuthentication(authCode.AuthenticatedAt, authCode.AuthenticationMethods, authCode.AssuranceLevel, authCode.SessionId);
        var accessToken = _tokenService.GenerateOAuthAccessToken(
            user, client.ClientId, claims.ApplicationCode, tokenScopes, claims.Roles, claims.Permissions, client.AccessTokenLifetimeSeconds, authentication,
            AudiencesFor(client.ClientId, resource, tokenScopes));
        var idToken = scopes.Contains(DomainConstants.OAuthScopes.OpenId)
            ? _tokenService.GenerateIdToken(user, client.ClientId, authCode.Nonce, scopes, authentication)
            : null;

        string? refreshToken = null;
        if (scopes.Contains(DomainConstants.OAuthScopes.OfflineAccess) && grantTypes.Contains("refresh_token"))
        {
            var generated = GenerateRefreshToken();
            refreshToken = generated.Raw;
            var absoluteExpiration = _dateTimeProvider.UtcNow.AddDays(_jwtSettings.RefreshTokenDays);
            _db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ApplicationCode = client.ApplicationSystem.Code,
                TokenHash = generated.Hash,
                OAuthClientId = client.ClientId,
                GrantedScopes = string.Join(" ", scopes),
                GrantedResources = granted.Resources.Count == 0 ? null : string.Join(" ", granted.Resources.Select(item => item.Identifier)),
                TokenFamilyId = Guid.NewGuid(),
                AbsoluteExpiresAt = absoluteExpiration,
                ExpiresAt = absoluteExpiration,
                CreatedAt = _dateTimeProvider.UtcNow,
                SessionId = authCode.SessionId,
                AuthenticatedAt = authCode.AuthenticatedAt,
                AuthenticationMethods = authCode.AuthenticationMethods,
                AssuranceLevel = authCode.AssuranceLevel
            });
        }

        AddAudit("OAUTH_CODE_EXCHANGED", user.Id, client, metadata: new { scopes, issuedRefreshToken = refreshToken is not null });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return OperationResult<OAuthTokenResponse>.Failure("CODE_ALREADY_USED", "Authorization code has already been used.");
        }

        return OperationResult<OAuthTokenResponse>.Success(new OAuthTokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = client.AccessTokenLifetimeSeconds,
            IdToken = idToken,
            RefreshToken = refreshToken,
            Scope = string.Join(" ", tokenScopes)
        });
    }

    public async Task<OperationResult<OAuthTokenResponse>> ClientCredentialsAsync(OAuthTokenRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT", "client_id is required.");

        var client = await FindActiveClientAsync(request.ClientId, ct);
        if (client is null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT", "Unknown, inactive, or unlinked OAuth client.");
        if (client.ClientType != OAuthClientType.Confidential)
            return OperationResult<OAuthTokenResponse>.Failure("UNAUTHORIZED_CLIENT", "client_credentials is only available to confidential clients.");

        var grantTypes = DeserializeValues(client.GrantTypesJson);
        if (!grantTypes.Contains("client_credentials", StringComparer.Ordinal))
            return OperationResult<OAuthTokenResponse>.Failure("UNAUTHORIZED_CLIENT", "This client is not authorized for client_credentials grant.");
        if (!SecretMatches(request.ClientSecret, client.HashedClientSecret))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT_CREDENTIALS", "Invalid client_id or client_secret.");

        var allowedScopes = DeserializeValues(client.AllowedScopesJson)
            .Where(scope => scope is not DomainConstants.OAuthScopes.OpenId and not DomainConstants.OAuthScopes.OfflineAccess)
            .ToList();
        var requestedScopes = string.IsNullOrWhiteSpace(request.Scope) ? allowedScopes : ParseScopes(request.Scope);
        if (requestedScopes.Count == 0 || requestedScopes.Any(scope => !allowedScopes.Contains(scope, StringComparer.Ordinal)))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_SCOPE", "One or more requested scopes are not allowed for client_credentials.");

        var granted = await ResolveResourcesAsync(requestedScopes, [], ct);
        if (granted.Error is { } resourceError)
            return OperationResult<OAuthTokenResponse>.Failure(resourceError.Code.ToUpperInvariant(), resourceError.Description);
        var (resource, targetError) = SelectResource(granted.Resources, request.Resource);
        if (targetError is not null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_TARGET", targetError.Description);
        requestedScopes = ScopesFor(requestedScopes, resource);
        if (requestedScopes.Count == 0)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_SCOPE", "None of the requested scopes applies to the resource.");

        var accessToken = _tokenService.GenerateOAuthAccessToken(
            null, client.ClientId, resource?.ApplicationCode ?? client.ApplicationSystem.Code, requestedScopes, [], [], client.AccessTokenLifetimeSeconds,
            audiences: AudiencesFor(client.ClientId, resource, requestedScopes));
        AddAudit("OAUTH_CLIENT_CREDENTIALS_ISSUED", null, client, metadata: new { scopes = requestedScopes, resource = resource?.Identifier });
        await _db.SaveChangesAsync(ct);

        return OperationResult<OAuthTokenResponse>.Success(new OAuthTokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = client.AccessTokenLifetimeSeconds,
            Scope = string.Join(" ", requestedScopes)
        });
    }

    public async Task<OperationResult<OAuthTokenResponse>> RefreshOAuthTokenAsync(OAuthTokenRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "refresh_token is required.");
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT", "client_id is required.");

        var client = await FindActiveClientAsync(request.ClientId, ct);
        if (client is null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT", "Unknown, inactive, or unlinked OAuth client.");

        var grants = DeserializeValues(client.GrantTypesJson);
        if (!grants.Contains("refresh_token", StringComparer.Ordinal))
            return OperationResult<OAuthTokenResponse>.Failure("UNAUTHORIZED_CLIENT", "This client is not authorized for refresh_token grant.");

        var clientAuthError = ValidateClientAuthentication(client, request.ClientSecret);
        if (clientAuthError is not null)
            return OperationResult<OAuthTokenResponse>.Failure(clientAuthError.Value.Code, clientAuthError.Value.Message);

        var tokenHash = _tokenService.HashToken(request.RefreshToken);
        var storedToken = await _db.RefreshTokens
            .IgnoreQueryFilters()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.OAuthClientId == client.ClientId, ct);

        if (storedToken is null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "Refresh token is invalid.");

        if (storedToken.RevokedAt is not null)
        {
            await RevokeOAuthFamilyAsync(storedToken, ct);
            AddAudit("OAUTH_REFRESH_TOKEN_REUSE_DETECTED", storedToken.UserId, client, metadata: new { familyId = storedToken.TokenFamilyId });
            await _db.SaveChangesAsync(ct);
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "Refresh token reuse was detected and the token family was revoked.");
        }

        var now = _dateTimeProvider.UtcNow;
        var absoluteExpiration = storedToken.AbsoluteExpiresAt ?? storedToken.ExpiresAt;
        if (storedToken.ExpiresAt <= now || absoluteExpiration <= now)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "Refresh token is expired.");
        if (!storedToken.User.IsActive || storedToken.User.DeletedAt is not null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "User account is inactive.");
        if (!await _userAccessService.HasActiveAccessAsync(storedToken.UserId, client.ApplicationSystemId, ct))
        {
            await RevokeOAuthFamilyAsync(storedToken, ct);
            AddAudit("OAUTH_REFRESH_ACCESS_REVOKED", storedToken.UserId, client);
            await _db.SaveChangesAsync(ct);
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "User no longer has access to this application.");
        }

        // The application's published policy still decides after the grant, like a first-party
        // refresh: group membership and time windows are current. The refresh request comes from
        // the client, so network conditions use the address of the user's sign-in session.
        var sessionAddress = storedToken.SessionId is { } sessionId
            ? await _db.RefreshTokens.AsNoTracking().Where(token => token.Id == sessionId).Select(token => token.IpAddress).FirstOrDefaultAsync(ct)
            : null;
        var grantAssurance = storedToken.AssuranceLevel is { } level && Enum.IsDefined(typeof(AuthenticationAssuranceLevel), level)
            ? (AuthenticationAssuranceLevel)level
            : AuthenticationAssuranceLevel.Password;
        var policy = await _accessPolicies.EvaluateAsync(new AccessPolicyEvaluationContext(
            storedToken.UserId, client.ApplicationSystemId, sessionAddress, now, AccessRiskLevel.Unknown, grantAssurance), ct);
        if (!policy.IsAllowed)
        {
            await RevokeOAuthFamilyAsync(storedToken, ct);
            AddAudit("ACCESS_POLICY_DENIED", storedToken.UserId, client, metadata: new { rule = policy.MatchedRuleName, ruleId = policy.MatchedRuleId, flow = "oauth_refresh" });
            await _db.SaveChangesAsync(ct);
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "The application's access policy no longer allows this grant.");
        }

        var scopes = ParseScopes(storedToken.GrantedScopes);
        var granted = await ResolveResourcesAsync(scopes, ParseScopes(storedToken.GrantedResources), ct);
        if (granted.Error is not null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "An API of this grant is no longer available.");
        var (resource, targetError) = SelectResource(granted.Resources, request.Resource);
        if (targetError is not null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_TARGET", targetError.Description);
        var claims = await TokenClaimsForAsync(storedToken.UserId, client, resource, ct);
        if (claims is null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "User no longer has access to the requested API.");

        storedToken.RevokedAt = now;
        var tokenScopes = ScopesFor(scopes, resource);
        var accessToken = _tokenService.GenerateOAuthAccessToken(
            storedToken.User, client.ClientId, claims.ApplicationCode, tokenScopes, claims.Roles, claims.Permissions, client.AccessTokenLifetimeSeconds,
            ToTokenAuthentication(storedToken.AuthenticatedAt, storedToken.AuthenticationMethods, storedToken.AssuranceLevel, storedToken.SessionId),
            AudiencesFor(client.ClientId, resource, tokenScopes));

        var generated = GenerateRefreshToken();
        storedToken.ReplacedByTokenHash = generated.Hash;
        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = storedToken.UserId,
            ApplicationCode = client.ApplicationSystem.Code,
            TokenHash = generated.Hash,
            OAuthClientId = client.ClientId,
            GrantedScopes = storedToken.GrantedScopes,
            GrantedResources = storedToken.GrantedResources,
            TokenFamilyId = storedToken.TokenFamilyId ?? Guid.NewGuid(),
            AbsoluteExpiresAt = absoluteExpiration,
            ExpiresAt = absoluteExpiration,
            CreatedAt = now,
            SessionId = storedToken.SessionId,
            AuthenticatedAt = storedToken.AuthenticatedAt,
            AuthenticationMethods = storedToken.AuthenticationMethods,
            AssuranceLevel = storedToken.AssuranceLevel
        });
        AddAudit("OAUTH_REFRESH_TOKEN_ROTATED", storedToken.UserId, client, metadata: new { familyId = storedToken.TokenFamilyId });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            await RevokeOAuthFamilyAsync(storedToken, ct);
            AddAudit("OAUTH_REFRESH_TOKEN_CONCURRENT_REUSE_DETECTED", storedToken.UserId, client, metadata: new { familyId = storedToken.TokenFamilyId });
            await _db.SaveChangesAsync(ct);
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "Refresh token reuse was detected and the token family was revoked.");
        }

        return OperationResult<OAuthTokenResponse>.Success(new OAuthTokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = client.AccessTokenLifetimeSeconds,
            RefreshToken = generated.Raw,
            Scope = string.Join(" ", tokenScopes)
        });
    }

    public async Task<OperationResult<OAuthTokenResponse>> TokenExchangeAsync(OAuthTokenRequest request, CancellationToken ct = default)
    {
        var caller = await AuthenticateConfidentialClientAsync(request.ClientId, request.ClientSecret, DomainConstants.OAuthGrantTypes.TokenExchange, ct);
        if (caller.Error is { } callerError)
            return OperationResult<OAuthTokenResponse>.Failure(callerError.Code, callerError.Description);
        var client = caller.Client!;

        if (!string.Equals(request.SubjectTokenType, DomainConstants.OAuthTokenTypes.AccessToken, StringComparison.Ordinal))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_REQUEST", "subject_token_type must be urn:ietf:params:oauth:token-type:access_token.");
        if (!string.IsNullOrEmpty(request.RequestedTokenType) &&
            !string.Equals(request.RequestedTokenType, DomainConstants.OAuthTokenTypes.AccessToken, StringComparison.Ordinal))
        {
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_REQUEST", "Only access tokens can be requested.");
        }

        var subject = _tokenService.ValidateAccessToken(request.SubjectToken ?? string.Empty);
        if (subject is null || !Guid.TryParse(subject.Subject, out var userId))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "subject_token is not an active user access token issued by AuthCenter.");

        // A client may only exchange tokens it received: issued to itself or to one of the APIs of its
        // own application. Anything else would let one client act with another client's tokens.
        var callerApis = await _db.ApiResources.AsNoTracking()
            .Where(resource => resource.ApplicationSystemId == client.ApplicationSystemId && resource.IsActive)
            .Select(resource => resource.Identifier)
            .ToListAsync(ct);
        if (!subject.Audiences.Any(audience => audience == client.ClientId || callerApis.Contains(audience, StringComparer.Ordinal)))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "subject_token was not issued to this client or its APIs.");

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null || !await IsSessionActiveAsync(subject.SessionId, userId, ct))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "The user or session behind subject_token is no longer active.");

        var target = !string.IsNullOrWhiteSpace(request.Resource) ? request.Resource : request.Audience;
        if (string.IsNullOrWhiteSpace(target))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_TARGET", "resource (or audience) names the API the new token is for.");
        var allowed = DeserializeValues(client.AllowedScopesJson);
        var targetScopes = await _db.ApiScopes.AsNoTracking()
            .Where(scope => scope.ApiResource.Identifier == target)
            .Select(scope => scope.Name)
            .ToListAsync(ct);
        var requested = string.IsNullOrWhiteSpace(request.Scope)
            ? targetScopes.Where(scope => allowed.Contains(scope, StringComparer.Ordinal)).ToList()
            : ParseScopes(request.Scope);
        if (requested.Count == 0 || requested.Any(scope => !allowed.Contains(scope, StringComparer.Ordinal) || !targetScopes.Contains(scope, StringComparer.Ordinal)))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_SCOPE", "The requested scopes are not allowed for this client on the target API.");

        var granted = await ResolveResourcesAsync(requested, [target], ct);
        if (granted.Error is { } resourceError)
            return OperationResult<OAuthTokenResponse>.Failure(resourceError.Code.ToUpperInvariant(), resourceError.Description);
        var resource = granted.Resources.Single();
        var claims = await TokenClaimsForAsync(userId, client, resource, ct);
        if (claims is null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "The user has no access to the target API.");

        // RFC 8693 section 4.1: the caller is the actor; a delegation chain already in the subject
        // token is kept nested under it.
        var actor = new Dictionary<string, object> { ["sub"] = client.ClientId };
        if (subject.Principal.FindFirst("act")?.Value is { Length: > 0 } previousActor)
        {
            try { actor["act"] = JsonSerializer.Deserialize<JsonElement>(previousActor); }
            catch (JsonException) { return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "subject_token has a malformed act claim."); }
        }

        // The new token never outlives the token it was exchanged for.
        var now = _dateTimeProvider.UtcNow;
        var lifetime = (int)Math.Min(client.AccessTokenLifetimeSeconds, Math.Max(0, (subject.ExpiresAt - now).TotalSeconds));
        if (lifetime < 1)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "subject_token is about to expire.");
        var authentication = SubjectAuthentication(subject);
        var accessToken = _tokenService.GenerateOAuthAccessToken(
            user, client.ClientId, claims.ApplicationCode, requested, claims.Roles, claims.Permissions, lifetime, authentication,
            [resource.Identifier], JsonSerializer.Serialize(actor));
        AddAudit("OAUTH_TOKEN_EXCHANGED", userId, client, metadata: new { subjectClient = subject.ClientId, resource = resource.Identifier, scopes = requested });
        await _db.SaveChangesAsync(ct);

        return OperationResult<OAuthTokenResponse>.Success(new OAuthTokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = lifetime,
            Scope = string.Join(" ", requested),
            IssuedTokenType = DomainConstants.OAuthTokenTypes.AccessToken
        });
    }

    public async Task<OperationResult<OAuthIntrospectionResponse>> IntrospectAsync(OAuthIntrospectionRequest request, CancellationToken ct = default)
    {
        var caller = await AuthenticateConfidentialClientAsync(request.ClientId, request.ClientSecret, grantType: null, ct);
        if (caller.Error is { } callerError)
            return OperationResult<OAuthIntrospectionResponse>.Failure(callerError.Code, callerError.Description);
        var client = caller.Client!;
        if (string.IsNullOrWhiteSpace(request.Token))
            return OperationResult<OAuthIntrospectionResponse>.Failure("INVALID_REQUEST", "token is required.");

        if (!string.Equals(request.TokenTypeHint, "refresh_token", StringComparison.Ordinal) &&
            _tokenService.ValidateAccessToken(request.Token) is { } accessToken)
        {
            // Only the client the token was issued to, or an API it was issued for, may inspect it.
            var callerApis = await _db.ApiResources.AsNoTracking()
                .Where(resource => resource.ApplicationSystemId == client.ApplicationSystemId && resource.IsActive)
                .Select(resource => resource.Identifier)
                .ToListAsync(ct);
            var entitled = accessToken.ClientId == client.ClientId ||
                accessToken.Audiences.Any(audience => audience == client.ClientId || callerApis.Contains(audience, StringComparer.Ordinal));
            if (!entitled || !await IsSubjectActiveAsync(accessToken, ct))
                return OperationResult<OAuthIntrospectionResponse>.Success(OAuthIntrospectionResponse.Inactive);

            return OperationResult<OAuthIntrospectionResponse>.Success(new OAuthIntrospectionResponse
            {
                Active = true,
                Scope = accessToken.Scope,
                ClientId = accessToken.ClientId,
                TokenType = "Bearer",
                ExpiresAt = new DateTimeOffset(accessToken.ExpiresAt).ToUnixTimeSeconds(),
                IssuedAt = accessToken.IssuedAt is { } issuedAt ? new DateTimeOffset(issuedAt).ToUnixTimeSeconds() : null,
                Subject = accessToken.Subject,
                Audience = accessToken.Audiences,
                Issuer = _jwtSettings.Issuer,
                TokenId = accessToken.TokenId
            });
        }

        // Refresh tokens are opaque: only the client that holds the grant can inspect it.
        var hash = _tokenService.HashToken(request.Token);
        var now = _dateTimeProvider.UtcNow;
        var refresh = await _db.RefreshTokens.AsNoTracking()
            .Where(token => token.TokenHash == hash && token.OAuthClientId == client.ClientId)
            .Select(token => new { token.UserId, token.GrantedScopes, token.CreatedAt, token.ExpiresAt, token.RevokedAt, UserActive = token.User.IsActive })
            .FirstOrDefaultAsync(ct);
        if (refresh is null || refresh.RevokedAt is not null || refresh.ExpiresAt <= now || !refresh.UserActive)
            return OperationResult<OAuthIntrospectionResponse>.Success(OAuthIntrospectionResponse.Inactive);
        return OperationResult<OAuthIntrospectionResponse>.Success(new OAuthIntrospectionResponse
        {
            Active = true,
            Scope = refresh.GrantedScopes,
            ClientId = client.ClientId,
            TokenType = "refresh_token",
            ExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(refresh.ExpiresAt, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            IssuedAt = new DateTimeOffset(DateTime.SpecifyKind(refresh.CreatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            Subject = refresh.UserId.ToString(),
            Issuer = _jwtSettings.Issuer
        });
    }

    private sealed record ClientAuthentication(OAuthClient? Client, ProtocolError? Error);

    private async Task<ClientAuthentication> AuthenticateConfidentialClientAsync(string? clientId, string? secret, string? grantType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return new ClientAuthentication(null, new ProtocolError("INVALID_CLIENT", "client_id is required."));
        var client = await FindActiveClientAsync(clientId, ct);
        if (client is null)
            return new ClientAuthentication(null, new ProtocolError("INVALID_CLIENT", "Unknown, inactive, or unlinked OAuth client."));
        if (client.ClientType != OAuthClientType.Confidential)
            return new ClientAuthentication(null, new ProtocolError("UNAUTHORIZED_CLIENT", "Only confidential clients can use this endpoint."));
        if (grantType is not null && !DeserializeValues(client.GrantTypesJson).Contains(grantType, StringComparer.Ordinal))
            return new ClientAuthentication(null, new ProtocolError("UNAUTHORIZED_CLIENT", $"This client is not authorized for {grantType}."));
        if (!SecretMatches(secret, client.HashedClientSecret))
            return new ClientAuthentication(null, new ProtocolError("INVALID_CLIENT_CREDENTIALS", "Invalid client_id or client_secret."));
        return new ClientAuthentication(client, null);
    }

    /// <summary>A user token stays active only while its user and single sign-on session do.</summary>
    private async Task<bool> IsSubjectActiveAsync(ValidatedAccessToken token, CancellationToken ct)
    {
        if (token.Subject is null)
            return true;
        if (!Guid.TryParse(token.Subject, out var userId))
            return false;
        var user = await _userManager.FindByIdAsync(userId.ToString());
        return user is not null && user.IsActive && user.DeletedAt is null && await IsSessionActiveAsync(token.SessionId, userId, ct);
    }

    private async Task<bool> IsSessionActiveAsync(string? sessionId, Guid userId, CancellationToken ct)
    {
        if (sessionId is null)
            return true;
        if (!Guid.TryParse(sessionId, out var id))
            return false;
        var now = _dateTimeProvider.UtcNow;
        return await _db.RefreshTokens.AsNoTracking().AnyAsync(
            token => token.Id == id && token.UserId == userId && token.RevokedAt == null && token.ExpiresAt > now, ct);
    }

    private static TokenAuthentication? SubjectAuthentication(ValidatedAccessToken subject)
    {
        if (!long.TryParse(subject.Principal.FindFirst("auth_time")?.Value, out var authTime))
            return null;
        var assurance = AuthenticationContext.AssuranceFor(subject.Principal.FindFirst("acr")?.Value ?? string.Empty) ?? AuthenticationAssuranceLevel.Password;
        return new TokenAuthentication(
            DateTimeOffset.FromUnixTimeSeconds(authTime).UtcDateTime,
            [],
            assurance,
            Guid.TryParse(subject.SessionId, out var sessionId) ? sessionId : null);
    }

    public async Task<OperationResult> RevokeTokenAsync(OAuthRevocationRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return OperationResult.Failure("INVALID_CLIENT", "client_id is required.");
        if (string.IsNullOrWhiteSpace(request.Token))
            return OperationResult.Failure("INVALID_REQUEST", "token is required.");

        var client = await FindActiveClientAsync(request.ClientId, ct);
        if (client is null)
            return OperationResult.Failure("INVALID_CLIENT", "Unknown, inactive, or unlinked OAuth client.");

        var clientAuthError = ValidateClientAuthentication(client, request.ClientSecret);
        if (clientAuthError is not null)
            return OperationResult.Failure(clientAuthError.Value.Code, clientAuthError.Value.Message);

        var tokenHash = _tokenService.HashToken(request.Token);
        var storedToken = await _db.RefreshTokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.OAuthClientId == client.ClientId, ct);

        // RFC 7009 requires a successful response for an unknown token to avoid leaking token state.
        if (storedToken is null)
            return OperationResult.Success();

        await RevokeOAuthFamilyAsync(storedToken, ct);
        AddAudit("OAUTH_TOKEN_REVOKED", storedToken.UserId, client, metadata: new { familyId = storedToken.TokenFamilyId });
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult<OAuthUserInfoResponse>> GetUserInfoAsync(
        Guid userId,
        string clientId,
        IList<string> scopes,
        CancellationToken ct = default)
    {
        var client = await FindActiveClientAsync(clientId, ct);
        if (client is null)
            return OperationResult<OAuthUserInfoResponse>.Failure("INVALID_CLIENT", "OAuth client is inactive or unlinked.");

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult<OAuthUserInfoResponse>.Failure("NOT_FOUND", "User not found.");
        if (!await _userAccessService.HasActiveAccessAsync(userId, client.ApplicationSystemId, ct))
            return OperationResult<OAuthUserInfoResponse>.Failure("ACCESS_DENIED", "User no longer has access to this application.");

        return OperationResult<OAuthUserInfoResponse>.Success(new OAuthUserInfoResponse
        {
            Sub = userId.ToString(),
            Name = scopes.Contains(DomainConstants.OAuthScopes.Profile) ? user.FullName : null,
            Email = scopes.Contains(DomainConstants.OAuthScopes.Email) ? user.Email : null,
            EmailVerified = scopes.Contains(DomainConstants.OAuthScopes.Email) ? user.EmailConfirmed : null
        });
    }

    private async Task<bool> HasConsentAsync(Guid userId, Guid clientId, IEnumerable<string> requestedScopes, CancellationToken ct)
    {
        var stored = await _db.OAuthConsentGrants
            .AsNoTracking()
            .Where(item => item.UserId == userId && item.OAuthClientId == clientId)
            .Select(item => item.ScopesJson)
            .SingleOrDefaultAsync(ct);
        if (stored is null)
            return false;
        var grantedScopes = DeserializeValues(stored).ToHashSet(StringComparer.Ordinal);
        return requestedScopes.All(grantedScopes.Contains);
    }

    private async Task UpsertConsentAsync(Guid userId, Guid clientId, IEnumerable<string> scopes, DateTime now, CancellationToken ct)
    {
        var grant = await _db.OAuthConsentGrants
            .SingleOrDefaultAsync(item => item.UserId == userId && item.OAuthClientId == clientId, ct);
        if (grant is null)
        {
            grant = new OAuthConsentGrant
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                OAuthClientId = clientId,
                GrantedAt = now
            };
            _db.OAuthConsentGrants.Add(grant);
        }

        var combined = DeserializeValues(grant.ScopesJson)
            .Concat(scopes)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
        grant.ScopesJson = JsonSerializer.Serialize(combined);
        grant.UpdatedAt = now;
    }

    private Task<OAuthClient?> FindActiveClientAsync(string clientId, CancellationToken ct) =>
        _db.OAuthClients
            .Include(client => client.ApplicationSystem)
            .FirstOrDefaultAsync(client =>
                client.ClientId == clientId &&
                client.IsActive &&
                client.ApplicationSystem.IsActive,
                ct);

    private async Task RevokeOAuthFamilyAsync(RefreshToken token, CancellationToken ct)
    {
        var now = _dateTimeProvider.UtcNow;
        var query = _db.RefreshTokens.Where(candidate =>
            candidate.OAuthClientId == token.OAuthClientId &&
            candidate.UserId == token.UserId &&
            candidate.RevokedAt == null);

        if (token.TokenFamilyId.HasValue)
            query = query.Where(candidate => candidate.TokenFamilyId == token.TokenFamilyId);

        if (_db.Database.IsRelational())
        {
            await query.ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.RevokedAt, now), ct);
            return;
        }

        var tokens = await query.ToListAsync(ct);
        foreach (var candidate in tokens)
            candidate.RevokedAt = now;
    }

    private void AddAudit(string action, Guid? userId, OAuthClient client, object? metadata = null)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ApplicationCode = client.ApplicationSystem.Code,
            Action = action,
            EntityName = nameof(OAuthClient),
            EntityId = client.ClientId,
            MetadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata),
            CreatedAt = _dateTimeProvider.UtcNow
        });
    }

    private (string Code, string Message)? ValidateClientAuthentication(OAuthClient client, string? providedSecret)
    {
        if (client.ClientType == OAuthClientType.Public)
            return string.IsNullOrWhiteSpace(providedSecret)
                ? null
                : ("INVALID_CLIENT_CREDENTIALS", "Public clients must not send a client_secret.");

        return SecretMatches(providedSecret, client.HashedClientSecret)
            ? null
            : ("INVALID_CLIENT_CREDENTIALS", "Invalid client credentials.");
    }

    private AuthorizationResponse SuccessResponse(string redirectUri, string? state, string responseMode, string code)
    {
        var parameters = new List<KeyValuePair<string, string>> { new("code", code) };
        if (!string.IsNullOrWhiteSpace(state))
            parameters.Add(new("state", state));
        parameters.Add(new("iss", _jwtSettings.Issuer));
        return new AuthorizationResponse { RedirectUri = redirectUri, ResponseMode = responseMode, Parameters = parameters };
    }

    private AuthorizationResponse ErrorResponse(string redirectUri, string? state, string responseMode, string error, string description)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("error", error),
            new("error_description", description)
        };
        if (!string.IsNullOrWhiteSpace(state))
            parameters.Add(new("state", state));
        parameters.Add(new("iss", _jwtSettings.Issuer));
        return new AuthorizationResponse { RedirectUri = redirectUri, ResponseMode = responseMode, Parameters = parameters };
    }

    private static TokenAuthentication? ToTokenAuthentication(DateTime? authenticatedAt, string? methods, int? assurance, Guid? sessionId) =>
        authenticatedAt is { } at
            ? new TokenAuthentication(
                at,
                AuthenticationContext.ParseMethods(methods),
                assurance is { } level && Enum.IsDefined(typeof(AuthenticationAssuranceLevel), level)
                    ? (AuthenticationAssuranceLevel)level
                    : AuthenticationAssuranceLevel.Password,
                sessionId)
            : null;

    private static OAuthAuthorizationSession? DeserializeSession(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return JsonSerializer.Deserialize<OAuthAuthorizationSession>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> DeserializeValues(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<string> ParseScopes(string? scopes) =>
        (scopes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private (string Raw, string Hash) GenerateRefreshToken()
    {
        var bytes = new byte[64];
        RandomNumberGenerator.Fill(bytes);
        var raw = Convert.ToBase64String(bytes);
        return (raw, _tokenService.HashToken(raw));
    }

    private static string GenerateCode()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string HashCode(string code) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static bool SecretMatches(string? providedSecret, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(providedSecret) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        byte[] stored;
        try
        {
            stored = Convert.FromBase64String(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        var provided = SHA256.HashData(Encoding.UTF8.GetBytes(providedSecret));
        return CryptographicOperations.FixedTimeEquals(provided, stored);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
