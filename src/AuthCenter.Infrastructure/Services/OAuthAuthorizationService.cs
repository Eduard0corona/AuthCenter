using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.OAuth;
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
    private const int AuthorizationLifetimeMinutes = 10;
    private static readonly Regex PkceChallengePattern = new("^[A-Za-z0-9_-]{43}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PkceVerifierPattern = new("^[A-Za-z0-9\\-._~]{43,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AuthCenterDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly ITransientStateStore _transientState;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IUserAccessService _userAccessService;
    private readonly IRoleService _roleService;
    private readonly JwtSettings _jwtSettings;

    public OAuthAuthorizationService(
        AuthCenterDbContext db,
        ITokenService tokenService,
        ITransientStateStore transientState,
        IDateTimeProvider dateTimeProvider,
        UserManager<ApplicationUser> userManager,
        IUserAccessService userAccessService,
        IRoleService roleService,
        IOptions<JwtSettings> jwtSettings)
    {
        _db = db;
        _tokenService = tokenService;
        _transientState = transientState;
        _dateTimeProvider = dateTimeProvider;
        _userManager = userManager;
        _userAccessService = userAccessService;
        _roleService = roleService;
        _jwtSettings = jwtSettings.Value;
    }

    public async Task<OperationResult<string>> InitiateAuthorizationAsync(AuthorizeRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return OperationResult<string>.Failure("INVALID_CLIENT", "client_id is required.");

        var client = await FindActiveClientAsync(request.ClientId, ct);
        if (client is null)
            return OperationResult<string>.Failure("INVALID_CLIENT", "Unknown, inactive, or unlinked OAuth client.");

        var allowedRedirectUris = DeserializeValues(client.RedirectUrisJson);
        if (string.IsNullOrWhiteSpace(request.RedirectUri) ||
            !allowedRedirectUris.Contains(request.RedirectUri, StringComparer.Ordinal))
        {
            return OperationResult<string>.Failure("INVALID_REDIRECT_URI", "The redirect_uri is not registered for this client.");
        }

        if (!string.Equals(request.ResponseType, "code", StringComparison.Ordinal))
            return OperationResult<string>.Success(BuildAuthorizationErrorRedirect(request.RedirectUri, request.State, "unsupported_response_type", "Only response_type=code is supported."));

        var grants = DeserializeValues(client.GrantTypesJson);
        if (!grants.Contains("authorization_code", StringComparer.Ordinal))
            return OperationResult<string>.Success(BuildAuthorizationErrorRedirect(request.RedirectUri, request.State, "unauthorized_client", "This client cannot use authorization_code."));

        var requestedScopes = ParseScopes(request.Scope);
        var allowedScopes = DeserializeValues(client.AllowedScopesJson);
        if (requestedScopes.Count == 0 || requestedScopes.Any(scope => !allowedScopes.Contains(scope, StringComparer.Ordinal)))
            return OperationResult<string>.Success(BuildAuthorizationErrorRedirect(request.RedirectUri, request.State, "invalid_scope", "One or more requested scopes are not allowed."));

        if (requestedScopes.Contains(DomainConstants.OAuthScopes.OfflineAccess) &&
            !grants.Contains("refresh_token", StringComparer.Ordinal))
        {
            return OperationResult<string>.Success(BuildAuthorizationErrorRedirect(request.RedirectUri, request.State, "invalid_scope", "offline_access requires the refresh_token grant."));
        }

        if (string.IsNullOrWhiteSpace(request.State))
            return OperationResult<string>.Success(BuildAuthorizationErrorRedirect(request.RedirectUri, null, "invalid_request", "state is required."));

        if (requestedScopes.Contains(DomainConstants.OAuthScopes.OpenId) && string.IsNullOrWhiteSpace(request.Nonce))
            return OperationResult<string>.Success(BuildAuthorizationErrorRedirect(request.RedirectUri, request.State, "invalid_request", "nonce is required for OpenID Connect requests."));

        if (client.RequirePkce || client.ClientType == OAuthClientType.Public)
        {
            if (string.IsNullOrWhiteSpace(request.CodeChallenge) || !PkceChallengePattern.IsMatch(request.CodeChallenge))
                return OperationResult<string>.Success(BuildAuthorizationErrorRedirect(request.RedirectUri, request.State, "invalid_request", "A valid S256 PKCE code_challenge is required."));

            if (!string.Equals(request.CodeChallengeMethod, "S256", StringComparison.Ordinal))
                return OperationResult<string>.Success(BuildAuthorizationErrorRedirect(request.RedirectUri, request.State, "invalid_request", "code_challenge_method must be S256."));
        }
        else if (!string.IsNullOrWhiteSpace(request.CodeChallenge))
        {
            if (!PkceChallengePattern.IsMatch(request.CodeChallenge) ||
                !string.Equals(request.CodeChallengeMethod, "S256", StringComparison.Ordinal))
            {
                return OperationResult<string>.Success(BuildAuthorizationErrorRedirect(request.RedirectUri, request.State, "invalid_request", "PKCE must use a valid S256 code_challenge."));
            }
        }

        var now = _dateTimeProvider.UtcNow;
        var interactionId = Guid.NewGuid().ToString("N");
        var session = new OAuthAuthorizationSession
        {
            ApplicationSystemId = client.ApplicationSystemId,
            ApplicationCode = client.ApplicationSystem.Code,
            ClientId = client.ClientId,
            RedirectUri = request.RedirectUri,
            Scopes = requestedScopes,
            State = request.State,
            CodeChallenge = request.CodeChallenge,
            CodeChallengeMethod = request.CodeChallengeMethod,
            Nonce = request.Nonce,
            CreatedAt = now
        };

        await _transientState.SetAsync(
            SessionPrefix,
            interactionId,
            JsonSerializer.Serialize(session),
            now.AddMinutes(AuthorizationLifetimeMinutes),
            ct);

        var separator = client.LoginUrl.Contains('?') ? '&' : '?';
        var redirectUrl = $"{client.LoginUrl}{separator}interaction_id={interactionId}&state={Uri.EscapeDataString(request.State)}";
        return OperationResult<string>.Success(redirectUrl);
    }

    public async Task<OperationResult<OAuthInteractionResponse>> GetInteractionAsync(string interactionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(interactionId))
            return OperationResult<OAuthInteractionResponse>.Failure("INVALID_INTERACTION", "Interaction not found or expired.");

        var storedSession = await _transientState.GetAsync(SessionPrefix, interactionId, ct);
        var session = DeserializeSession(storedSession);
        if (session is null)
            return OperationResult<OAuthInteractionResponse>.Failure("INVALID_INTERACTION", "Interaction not found or expired.");

        var client = await FindActiveClientAsync(session.ClientId, ct);
        if (client is null || client.ApplicationSystemId != session.ApplicationSystemId)
            return OperationResult<OAuthInteractionResponse>.Failure("INVALID_CLIENT", "OAuth client is no longer active.");

        return OperationResult<OAuthInteractionResponse>.Success(new OAuthInteractionResponse
        {
            ClientId = client.ClientId,
            ClientDisplayName = client.DisplayName,
            ApplicationCode = client.ApplicationSystem.Code,
            ApplicationName = client.ApplicationSystem.Name,
            Scopes = session.Scopes,
            RequiresConsent = !client.AutoConsent,
            ExpiresAt = session.CreatedAt.AddMinutes(AuthorizationLifetimeMinutes)
        });
    }

    public async Task<OperationResult<string>> CompleteAuthorizationAsync(CompleteAuthorizationRequest request, Guid userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.InteractionId))
            return OperationResult<string>.Failure("INVALID_INTERACTION", "Interaction not found or expired. Start a new authorization request.");

        var storedSession = await _transientState.TakeAsync(SessionPrefix, request.InteractionId, ct);
        var session = DeserializeSession(storedSession);
        if (session is null)
            return OperationResult<string>.Failure("INVALID_INTERACTION", "Interaction not found or expired. Start a new authorization request.");

        var client = await FindActiveClientAsync(session.ClientId, ct);
        if (client is null || client.ApplicationSystemId != session.ApplicationSystemId)
            return OperationResult<string>.Failure("INVALID_CLIENT", "OAuth client is no longer active.");

        if (!request.Consent && !client.AutoConsent)
        {
            AddAudit("OAUTH_CONSENT_DENIED", userId, client, metadata: new { scopes = session.Scopes });
            await _db.SaveChangesAsync(ct);
            return OperationResult<string>.Success(BuildAuthorizationErrorRedirect(session.RedirectUri, session.State, "access_denied", "The resource owner denied the request."));
        }

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult<string>.Failure("ACCESS_DENIED", "The authenticated account is inactive.");

        if (!await _userAccessService.HasActiveAccessAsync(userId, client.ApplicationSystemId, ct))
        {
            AddAudit("OAUTH_ACCESS_DENIED", userId, client, metadata: new { reason = "NoApplicationAccess" });
            await _db.SaveChangesAsync(ct);
            return OperationResult<string>.Success(BuildAuthorizationErrorRedirect(session.RedirectUri, session.State, "access_denied", "The user does not have access to this application."));
        }

        var rawCode = GenerateCode();
        var now = _dateTimeProvider.UtcNow;
        _db.OAuthAuthorizationCodes.Add(new OAuthAuthorizationCode
        {
            Id = Guid.NewGuid(),
            CodeHash = HashCode(rawCode),
            OAuthClientId = client.Id,
            UserId = userId,
            RedirectUri = session.RedirectUri,
            ScopesJson = JsonSerializer.Serialize(session.Scopes),
            CodeChallenge = session.CodeChallenge,
            CodeChallengeMethod = session.CodeChallengeMethod,
            Nonce = session.Nonce,
            ExpiresAt = now.AddMinutes(AuthorizationLifetimeMinutes),
            IsUsed = false,
            CreatedAt = now
        });
        AddAudit("OAUTH_AUTHORIZATION_GRANTED", userId, client, metadata: new { scopes = session.Scopes });
        await _db.SaveChangesAsync(ct);

        return OperationResult<string>.Success(BuildAuthorizationSuccessRedirect(session.RedirectUri, session.State, rawCode));
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

        authCode.IsUsed = true;
        var scopes = DeserializeValues(authCode.ScopesJson);
        var roles = await _roleService.GetRoleNamesForUserAsync(user.Id, client.ApplicationSystemId, ct);
        var permissions = await _roleService.GetPermissionCodesForUserAsync(user.Id, client.ApplicationSystemId, ct);
        var accessToken = _tokenService.GenerateOAuthAccessToken(
            user, client.ClientId, client.ApplicationSystem.Code, scopes, roles, permissions, client.AccessTokenLifetimeSeconds);
        var idToken = scopes.Contains(DomainConstants.OAuthScopes.OpenId)
            ? _tokenService.GenerateIdToken(user, client.ClientId, authCode.Nonce, scopes)
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
                TokenFamilyId = Guid.NewGuid(),
                AbsoluteExpiresAt = absoluteExpiration,
                ExpiresAt = absoluteExpiration,
                CreatedAt = _dateTimeProvider.UtcNow
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
            Scope = string.Join(" ", scopes)
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

        var accessToken = _tokenService.GenerateOAuthAccessToken(
            null, client.ClientId, client.ApplicationSystem.Code, requestedScopes, [], [], client.AccessTokenLifetimeSeconds);
        AddAudit("OAUTH_CLIENT_CREDENTIALS_ISSUED", null, client, metadata: new { scopes = requestedScopes });
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

        storedToken.RevokedAt = now;
        var scopes = ParseScopes(storedToken.GrantedScopes);
        var roles = await _roleService.GetRoleNamesForUserAsync(storedToken.UserId, client.ApplicationSystemId, ct);
        var permissions = await _roleService.GetPermissionCodesForUserAsync(storedToken.UserId, client.ApplicationSystemId, ct);
        var accessToken = _tokenService.GenerateOAuthAccessToken(
            storedToken.User, client.ClientId, client.ApplicationSystem.Code, scopes, roles, permissions, client.AccessTokenLifetimeSeconds);

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
            TokenFamilyId = storedToken.TokenFamilyId ?? Guid.NewGuid(),
            AbsoluteExpiresAt = absoluteExpiration,
            ExpiresAt = absoluteExpiration,
            CreatedAt = now
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
            Scope = string.Join(" ", scopes)
        });
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

    private string BuildAuthorizationSuccessRedirect(string redirectUri, string? state, string code)
    {
        var parameters = new List<(string Key, string Value)> { ("code", code) };
        if (!string.IsNullOrWhiteSpace(state))
            parameters.Add(("state", state));
        parameters.Add(("iss", _jwtSettings.Issuer));
        return AppendQuery(redirectUri, parameters);
    }

    private string BuildAuthorizationErrorRedirect(string redirectUri, string? state, string error, string description)
    {
        var parameters = new List<(string Key, string Value)>
        {
            ("error", error),
            ("error_description", description)
        };
        if (!string.IsNullOrWhiteSpace(state))
            parameters.Add(("state", state));
        parameters.Add(("iss", _jwtSettings.Issuer));
        return AppendQuery(redirectUri, parameters);
    }

    private static string AppendQuery(string uri, IEnumerable<(string Key, string Value)> parameters)
    {
        var separator = uri.Contains('?') ? '&' : '?';
        return uri + separator + string.Join("&", parameters.Select(parameter =>
            $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
    }

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
