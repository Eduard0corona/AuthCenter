using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Contracts.Responses.OAuth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AuthCenter.Infrastructure.Services;

public class OAuthAuthorizationService : IOAuthAuthorizationService
{
    private readonly AuthCenterDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IMemoryCache _cache;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly UserManager<ApplicationUser> _userManager;

    private const string SessionPrefix = "oauth_session:";

    public OAuthAuthorizationService(
        AuthCenterDbContext db,
        ITokenService tokenService,
        IMemoryCache cache,
        IDateTimeProvider dateTimeProvider,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _tokenService = tokenService;
        _cache = cache;
        _dateTimeProvider = dateTimeProvider;
        _userManager = userManager;
    }

    public async Task<OperationResult<string>> InitiateAuthorizationAsync(AuthorizeRequest request, CancellationToken ct = default)
    {
        if (!string.Equals(request.ResponseType, "code", StringComparison.Ordinal))
            return OperationResult<string>.Failure("UNSUPPORTED_RESPONSE_TYPE", "Only response_type=code is supported.");

        if (string.IsNullOrWhiteSpace(request.ClientId))
            return OperationResult<string>.Failure("INVALID_CLIENT", "client_id is required.");

        var client = await _db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == request.ClientId && c.IsActive, ct);
        if (client is null)
            return OperationResult<string>.Failure("INVALID_CLIENT", "Unknown or inactive OAuth client.");

        var allowedRedirectUris = JsonSerializer.Deserialize<List<string>>(client.RedirectUrisJson) ?? [];
        if (string.IsNullOrWhiteSpace(request.RedirectUri) || !allowedRedirectUris.Contains(request.RedirectUri))
            return OperationResult<string>.Failure("INVALID_REDIRECT_URI", "The redirect_uri is not registered for this client.");

        var allowedScopes = JsonSerializer.Deserialize<List<string>>(client.AllowedScopesJson) ?? [];
        var requestedScopes = (request.Scope ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Intersect(allowedScopes)
            .ToList();

        if (requestedScopes.Count == 0)
            return OperationResult<string>.Failure("INVALID_SCOPE", "None of the requested scopes are allowed for this client.");

        if (client.RequirePkce && string.IsNullOrWhiteSpace(request.CodeChallenge))
            return OperationResult<string>.Failure("PKCE_REQUIRED", "This client requires PKCE. Provide code_challenge and code_challenge_method=S256.");

        if (!string.IsNullOrWhiteSpace(request.CodeChallengeMethod) &&
            !string.Equals(request.CodeChallengeMethod, "S256", StringComparison.Ordinal))
        {
            return OperationResult<string>.Failure("UNSUPPORTED_CODE_CHALLENGE_METHOD", "Only code_challenge_method=S256 is supported.");
        }

        var interactionId = Guid.NewGuid().ToString("N");
        var session = new OAuthAuthorizationSession
        {
            ClientId = client.ClientId,
            RedirectUri = request.RedirectUri,
            Scopes = requestedScopes,
            State = request.State,
            CodeChallenge = request.CodeChallenge,
            CodeChallengeMethod = request.CodeChallengeMethod,
            Nonce = request.Nonce,
            CreatedAt = _dateTimeProvider.UtcNow
        };

        _cache.Set($"{SessionPrefix}{interactionId}", session, TimeSpan.FromMinutes(10));

        var separator = client.LoginUrl.Contains('?') ? '&' : '?';
        var redirectUrl = $"{client.LoginUrl}{separator}interaction_id={interactionId}";
        if (!string.IsNullOrWhiteSpace(request.State))
            redirectUrl += $"&state={Uri.EscapeDataString(request.State)}";

        return OperationResult<string>.Success(redirectUrl);
    }

    public async Task<OperationResult<string>> CompleteAuthorizationAsync(CompleteAuthorizationRequest request, Guid userId, CancellationToken ct = default)
    {
        var cacheKey = $"{SessionPrefix}{request.InteractionId}";
        if (!_cache.TryGetValue(cacheKey, out OAuthAuthorizationSession? session) || session is null)
            return OperationResult<string>.Failure("INVALID_INTERACTION", "Interaction not found or expired. Start a new authorization request.");

        _cache.Remove(cacheKey);

        if (!request.Consent)
            return OperationResult<string>.Failure("ACCESS_DENIED", "User denied consent.");

        var client = await _db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == session.ClientId && c.IsActive, ct);
        if (client is null)
            return OperationResult<string>.Failure("INVALID_CLIENT", "OAuth client is no longer active.");

        var rawCode = GenerateCode();
        var codeHash = HashCode(rawCode);

        var authCode = new OAuthAuthorizationCode
        {
            Id = Guid.NewGuid(),
            CodeHash = codeHash,
            OAuthClientId = client.Id,
            UserId = userId,
            RedirectUri = session.RedirectUri,
            ScopesJson = JsonSerializer.Serialize(session.Scopes),
            CodeChallenge = session.CodeChallenge,
            CodeChallengeMethod = session.CodeChallengeMethod,
            Nonce = session.Nonce,
            ExpiresAt = _dateTimeProvider.UtcNow.AddMinutes(10),
            IsUsed = false,
            CreatedAt = _dateTimeProvider.UtcNow
        };

        _db.OAuthAuthorizationCodes.Add(authCode);
        await _db.SaveChangesAsync(ct);

        var separator = session.RedirectUri.Contains('?') ? '&' : '?';
        var redirectUrl = $"{session.RedirectUri}{separator}code={Uri.EscapeDataString(rawCode)}";
        if (!string.IsNullOrWhiteSpace(session.State))
            redirectUrl += $"&state={Uri.EscapeDataString(session.State)}";

        return OperationResult<string>.Success(redirectUrl);
    }

    public async Task<OperationResult<OAuthTokenResponse>> ExchangeCodeAsync(OAuthTokenRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT", "client_id is required.");

        var client = await _db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == request.ClientId && c.IsActive, ct);
        if (client is null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT", "Unknown or inactive OAuth client.");

        var grantTypes = JsonSerializer.Deserialize<List<string>>(client.GrantTypesJson) ?? [];
        if (!grantTypes.Contains("authorization_code"))
            return OperationResult<OAuthTokenResponse>.Failure("UNAUTHORIZED_CLIENT", "This client is not authorized for authorization_code grant.");

        if (client.ClientType == OAuthClientType.Confidential)
        {
            if (string.IsNullOrWhiteSpace(request.ClientSecret))
                return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT_CREDENTIALS", "client_secret is required for confidential clients.");

            if (!SecretMatches(request.ClientSecret, client.HashedClientSecret))
                return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT_CREDENTIALS", "Invalid client_secret.");
        }

        if (string.IsNullOrWhiteSpace(request.Code))
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "code is required.");

        var codeHash = HashCode(request.Code);
        var authCode = await _db.OAuthAuthorizationCodes
            .FirstOrDefaultAsync(c => c.CodeHash == codeHash, ct);

        if (authCode is null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "Authorization code not found.");
        if (authCode.IsUsed)
            return OperationResult<OAuthTokenResponse>.Failure("CODE_ALREADY_USED", "Authorization code has already been used.");
        if (authCode.ExpiresAt < _dateTimeProvider.UtcNow)
            return OperationResult<OAuthTokenResponse>.Failure("CODE_EXPIRED", "Authorization code has expired.");
        if (!string.Equals(authCode.RedirectUri, request.RedirectUri, StringComparison.Ordinal))
            return OperationResult<OAuthTokenResponse>.Failure("REDIRECT_URI_MISMATCH", "redirect_uri does not match the authorization request.");
        if (authCode.OAuthClientId != client.Id)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "Authorization code was not issued to this client.");

        if (!string.IsNullOrWhiteSpace(authCode.CodeChallenge))
        {
            if (string.IsNullOrWhiteSpace(request.CodeVerifier))
                return OperationResult<OAuthTokenResponse>.Failure("CODE_VERIFIER_REQUIRED", "code_verifier is required for this authorization code.");

            var computedChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier)));
            if (!string.Equals(computedChallenge, authCode.CodeChallenge, StringComparison.Ordinal))
                return OperationResult<OAuthTokenResponse>.Failure("INVALID_CODE_VERIFIER", "code_verifier does not match the code_challenge.");
        }

        authCode.IsUsed = true;
        await _db.SaveChangesAsync(ct);

        var user = await _userManager.FindByIdAsync(authCode.UserId.ToString());
        if (user is null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "User not found.");

        var scopes = JsonSerializer.Deserialize<List<string>>(authCode.ScopesJson) ?? [];
        var accessToken = _tokenService.GenerateOAuthAccessToken(user, client.ClientId, scopes, client.AccessTokenLifetimeSeconds);
        var idToken = scopes.Contains(DomainConstants.OAuthScopes.OpenId)
            ? _tokenService.GenerateIdToken(user, client.ClientId, authCode.Nonce, scopes)
            : null;

        string? refreshToken = null;
        if (scopes.Contains(DomainConstants.OAuthScopes.OfflineAccess) && grantTypes.Contains("refresh_token"))
        {
            var rtBytes = new byte[32];
            RandomNumberGenerator.Fill(rtBytes);
            refreshToken = Convert.ToBase64String(rtBytes);
            var rtHash = _tokenService.HashToken(refreshToken);

            _db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ApplicationCode = client.ClientId,
                TokenHash = rtHash,
                OAuthClientId = client.ClientId,
                GrantedScopes = string.Join(" ", scopes),
                ExpiresAt = _dateTimeProvider.UtcNow.AddDays(30),
                CreatedAt = _dateTimeProvider.UtcNow
            });
            await _db.SaveChangesAsync(ct);
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

        var client = await _db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == request.ClientId && c.IsActive, ct);
        if (client is null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT", "Unknown or inactive OAuth client.");

        if (client.ClientType != Domain.Enums.OAuthClientType.Confidential)
            return OperationResult<OAuthTokenResponse>.Failure("UNAUTHORIZED_CLIENT", "client_credentials grant is only available to Confidential clients.");

        var grantTypes = JsonSerializer.Deserialize<List<string>>(client.GrantTypesJson) ?? [];
        if (!grantTypes.Contains("client_credentials"))
            return OperationResult<OAuthTokenResponse>.Failure("UNAUTHORIZED_CLIENT", "This client is not authorized for client_credentials grant.");

        if (!SecretMatches(request.ClientSecret, client.HashedClientSecret))
        {
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT_CREDENTIALS", "Invalid client_id or client_secret.");
        }

        var allowedScopes = JsonSerializer.Deserialize<List<string>>(client.AllowedScopesJson) ?? [];
        var requestedScopes = (request.Scope ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Intersect(allowedScopes)
            .ToList();

        if (requestedScopes.Count == 0)
            requestedScopes = allowedScopes;

        var accessToken = _tokenService.GenerateOAuthAccessToken(null, client.ClientId, requestedScopes, client.AccessTokenLifetimeSeconds);

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

        var client = await _db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == request.ClientId && c.IsActive, ct);
        if (client is null)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT", "Unknown or inactive OAuth client.");

        if (client.ClientType == OAuthClientType.Confidential)
        {
            if (!SecretMatches(request.ClientSecret, client.HashedClientSecret))
                return OperationResult<OAuthTokenResponse>.Failure("INVALID_CLIENT_CREDENTIALS", "Invalid client_secret.");
        }

        var tokenHash = _tokenService.HashToken(request.RefreshToken);
        var storedToken = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.OAuthClientId == client.ClientId, ct);

        if (storedToken is null || !storedToken.IsActive)
            return OperationResult<OAuthTokenResponse>.Failure("INVALID_GRANT", "Refresh token is invalid, expired, or revoked.");

        storedToken.RevokedAt = _dateTimeProvider.UtcNow;

        var scopes = (storedToken.GrantedScopes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var user = storedToken.User;
        var accessToken = _tokenService.GenerateOAuthAccessToken(user, client.ClientId, scopes, client.AccessTokenLifetimeSeconds);
        var idToken = scopes.Contains(DomainConstants.OAuthScopes.OpenId)
            ? _tokenService.GenerateIdToken(user, client.ClientId, null, scopes)
            : null;

        var rtBytes = new byte[32];
        RandomNumberGenerator.Fill(rtBytes);
        var newRefreshToken = Convert.ToBase64String(rtBytes);
        var newRtHash = _tokenService.HashToken(newRefreshToken);

        storedToken.ReplacedByTokenHash = newRtHash;

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ApplicationCode = client.ClientId,
            TokenHash = newRtHash,
            OAuthClientId = client.ClientId,
            GrantedScopes = storedToken.GrantedScopes,
            ExpiresAt = _dateTimeProvider.UtcNow.AddDays(30),
            CreatedAt = _dateTimeProvider.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        return OperationResult<OAuthTokenResponse>.Success(new OAuthTokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = client.AccessTokenLifetimeSeconds,
            IdToken = idToken,
            RefreshToken = newRefreshToken,
            Scope = string.Join(" ", scopes)
        });
    }

    public async Task<OperationResult<OAuthUserInfoResponse>> GetUserInfoAsync(Guid userId, IList<string> scopes, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult<OAuthUserInfoResponse>.Failure("NOT_FOUND", "User not found.");

        var response = new OAuthUserInfoResponse
        {
            Sub = userId.ToString(),
            Name = scopes.Contains(DomainConstants.OAuthScopes.Profile) ? user.FullName : null,
            Email = scopes.Contains(DomainConstants.OAuthScopes.Email) ? user.Email : null,
            EmailVerified = scopes.Contains(DomainConstants.OAuthScopes.Email) ? user.EmailConfirmed : null
        };

        return OperationResult<OAuthUserInfoResponse>.Success(response);
    }

    private static string GenerateCode()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string HashCode(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return Convert.ToBase64String(bytes);
    }

    private static string HashSecret(string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Compares in constant time so the answer does not leak through how long it took to reach it.
    /// </summary>
    private static bool SecretMatches(string? providedSecret, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(providedSecret) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        var provided = SHA256.HashData(Encoding.UTF8.GetBytes(providedSecret));

        byte[] stored;
        try
        {
            stored = Convert.FromBase64String(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(provided, stored);
    }

    private static string Base64UrlEncode(byte[] input)
        => Convert.ToBase64String(input).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
