using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Federation;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Federation;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AuthCenter.Infrastructure.Services;

public sealed partial class FederationService
{
    private const string OidcStatePurpose = "federation_oidc_state";
    private static readonly TimeSpan UpstreamLifetime = TimeSpan.FromMinutes(10);
    private static readonly string[] AcceptedIdTokenAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512
    ];

    /// <summary>JSON API: the caller's own callback receives the upstream code and posts it to <see cref="CompleteOidcAsync"/>.</summary>
    public async Task<OperationResult<OidcFederationChallengeResponse>> BeginOidcAsync(BeginOidcFederationRequest request, CancellationToken ct = default)
    {
        var provider = await _db.FederationProviders.AsNoTracking().FirstOrDefaultAsync(item => item.Id == request.ProviderId && item.IsActive && item.Protocol == FederationProtocol.Oidc, ct);
        if (provider is null) return OperationResult<OidcFederationChallengeResponse>.Failure("FEDERATION_PROVIDER_NOT_FOUND", "Active OIDC provider not found.");
        var interactionId = Guid.NewGuid().ToString("N");
        var challenge = await BuildOidcChallengeAsync(provider, new FederationTransaction { ProviderId = provider.Id, ApiInteractionId = interactionId }, request.LoginHint, forceAuthentication: false, ct);
        if (!challenge.IsSuccess) return OperationResult<OidcFederationChallengeResponse>.Failure(challenge.ErrorCode!, challenge.Message!);
        return OperationResult<OidcFederationChallengeResponse>.Success(new OidcFederationChallengeResponse
        {
            InteractionId = interactionId,
            AuthorizationUrl = challenge.Data!,
            ExpiresIn = (int)UpstreamLifetime.TotalSeconds
        });
    }

    /// <summary>JSON API completion; the application's policy and MFA gate decide what is issued.</summary>
    public async Task<OperationResult<AuthResponse>> CompleteOidcAsync(CompleteOidcFederationRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var transaction = IsStateValue(request.State) ? ReadTransaction(await _state.TakeAsync(OidcStatePurpose, request.State, ct)) : null;
        if (transaction is null || transaction.Hosted || !FixedEquals(transaction.ApiInteractionId, request.InteractionId) || string.IsNullOrWhiteSpace(request.Code))
            return OperationResult<AuthResponse>.Failure("INVALID_OIDC_STATE", "OIDC interaction is invalid, expired, or already used.");
        var authenticated = await AuthenticateOidcAsync(transaction, request.Code, ipAddress, userAgent, ct);
        if (!authenticated.IsSuccess)
            return OperationResult<AuthResponse>.Failure(authenticated.ErrorCode!, authenticated.Message!);
        var (user, provider, authentication) = authenticated.Data!;
        return await _auth.CompleteFederatedSignInAsync(user.Id, provider.ApplicationSystem.Code, authentication, ipAddress, userAgent, ct);
    }

    private async Task<OperationResult<string>> BuildOidcChallengeAsync(FederationProvider provider, FederationTransaction transaction, string? loginHint, bool forceAuthentication, CancellationToken ct)
    {
        var configuration = await GetOidcConfigurationAsync(provider, refresh: false, ct);
        if (configuration is null || !IsHttps(configuration.AuthorizationEndpoint))
            return OperationResult<string>.Failure("OIDC_DISCOVERY_FAILED", "OIDC discovery document is unavailable or incomplete.");

        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        await _state.SetAsync(OidcStatePurpose, state, JsonSerializer.Serialize(transaction with { Nonce = nonce, Verifier = verifier }), _clock.UtcNow.Add(UpstreamLifetime), ct);

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
            ["login_hint"] = string.IsNullOrWhiteSpace(loginHint) || loginHint.Length > 256 ? null : loginHint.Trim(),
            // A request that needs a fresh sign-in must not be answered by the upstream's own session.
            ["prompt"] = forceAuthentication ? "login" : null
        };
        return OperationResult<string>.Success(QueryHelpers.AddQueryString(configuration.AuthorizationEndpoint, query));
    }

    /// <summary>Redeems the upstream code, validates the ID token and maps the identity to a user.</summary>
    private async Task<OperationResult<FederatedAuthentication>> AuthenticateOidcAsync(FederationTransaction transaction, string code, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        var provider = await _db.FederationProviders.Include(item => item.ApplicationSystem)
            .FirstOrDefaultAsync(item => item.Id == transaction.ProviderId && item.IsActive && item.Protocol == FederationProtocol.Oidc, ct);
        if (provider is null)
            return OperationResult<FederatedAuthentication>.Failure("FEDERATION_PROVIDER_NOT_FOUND", "Federation provider not found.");
        var configuration = await GetOidcConfigurationAsync(provider, refresh: false, ct);
        if (configuration is null || !IsHttps(configuration.TokenEndpoint))
            return await FailAsync(provider, "OIDC_DISCOVERY_FAILED", "OIDC token endpoint is unavailable.", "OIDC", ipAddress, userAgent, ct);

        var idToken = await RedeemCodeAsync(provider, configuration, code, transaction.Verifier ?? string.Empty, ct);
        if (string.IsNullOrWhiteSpace(idToken))
            return await FailAsync(provider, "OIDC_CODE_REJECTED", "The upstream provider rejected the authorization code.", "OIDC", ipAddress, userAgent, ct);
        var principal = ValidateIdToken(idToken, provider, configuration, transaction.Nonce ?? string.Empty);
        if (principal is null)
        {
            // The upstream may have rotated its keys since the metadata was cached.
            configuration = await GetOidcConfigurationAsync(provider, refresh: true, ct);
            principal = configuration is null ? null : ValidateIdToken(idToken, provider, configuration, transaction.Nonce ?? string.Empty);
        }
        if (principal is null)
            return await FailAsync(provider, "INVALID_OIDC_TOKEN", "The upstream ID token failed signature, issuer, audience, lifetime, or nonce validation.", "OIDC", ipAddress, userAgent, ct);

        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var email = principal.FindFirstValue(JwtRegisteredClaimNames.Email)?.Trim();
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
            return await FailAsync(provider, "OIDC_CLAIMS_INCOMPLETE", "A stable subject and an email are required.", "OIDC", ipAddress, userAgent, ct);

        // email_verified is required by default; a provider trusted for its routed domains may omit it.
        var emailVerified = string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase) ||
            (!provider.RequireVerifiedEmail && await IsRoutedDomainAsync(provider.Id, email, ct));
        var identity = new UpstreamIdentity(
            subject,
            email,
            principal.FindFirstValue("name") ?? email,
            emailVerified,
            principal.FindAll("amr").Any(claim => string.Equals(claim.Value, "mfa", StringComparison.Ordinal)),
            OidcGroups(provider, principal));
        return await AuthenticateUpstreamIdentityAsync(provider, identity, "OIDC", ipAddress, userAgent, ct);
    }

    /// <summary>
    /// Values of the groups claim. When the upstream moved the groups out of the token (Entra ID
    /// "overage" via <c>_claim_names</c>) memberships are left untouched rather than cleared.
    /// </summary>
    private static IReadOnlyCollection<string>? OidcGroups(FederationProvider provider, ClaimsPrincipal principal)
    {
        if (string.IsNullOrWhiteSpace(provider.GroupsClaim))
            return null;
        if (principal.FindFirstValue("_claim_names") is { } distributed && distributed.Contains($"\"{provider.GroupsClaim}\"", StringComparison.Ordinal))
            return null;
        return principal.FindAll(provider.GroupsClaim).Select(claim => claim.Value.Trim()).Where(value => value.Length > 0).ToList();
    }

    private async Task<bool> IsRoutedDomainAsync(Guid providerId, string email, CancellationToken ct)
    {
        var domain = FederationDomains.OfEmail(email);
        return domain is not null && await _db.FederationRoutingRules.AnyAsync(
            rule => rule.FederationProviderId == providerId && rule.IsActive && rule.EmailDomain == domain, ct);
    }

    private Task<OpenIdConnectConfiguration?> GetOidcConfigurationAsync(FederationProvider provider, bool refresh, CancellationToken ct)
    {
        var address = provider.DiscoveryEndpoint ?? $"{provider.Issuer.TrimEnd('/')}/.well-known/openid-configuration";
        return _metadata.GetAsync(provider.Id, address, refresh, ct);
    }

    private async Task<string?> RedeemCodeAsync(FederationProvider provider, OpenIdConnectConfiguration configuration, string code, string verifier, CancellationToken ct)
    {
        var values = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = provider.OidcCallbackUrl!, ["code_verifier"] = verifier };
        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.TokenEndpoint);
        var secret = string.IsNullOrWhiteSpace(provider.ProtectedClientSecret) ? null : _secrets.Unprotect(provider.ProtectedClientSecret);
        // client_secret_post unless the upstream only supports client_secret_basic.
        var methods = configuration.TokenEndpointAuthMethodsSupported;
        if (secret is not null && methods.Contains("client_secret_basic") && !methods.Contains("client_secret_post"))
        {
            var credentials = $"{Uri.EscapeDataString(provider.ClientId!)}:{Uri.EscapeDataString(secret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }
        else
        {
            values["client_id"] = provider.ClientId!;
            if (secret is not null) values["client_secret"] = secret;
        }
        request.Content = new FormUrlEncodedContent(values);
        try
        {
            using var response = await _httpClients.CreateClient(FederationMetadataCache.HttpClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("id_token", out var token) && token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    private static ClaimsPrincipal? ValidateIdToken(string token, FederationProvider provider, OpenIdConnectConfiguration configuration, string nonce)
    {
        try
        {
            // Issuers are compared as configured, tolerating only a trailing slash difference.
            var issuer = provider.Issuer.TrimEnd('/');
            var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = [issuer, issuer + "/"],
                ValidateAudience = true,
                ValidAudience = provider.ClientId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = configuration.SigningKeys,
                ValidAlgorithms = AcceptedIdTokenAlgorithms,
                ClockSkew = TimeSpan.FromMinutes(2),
                NameClaimType = "name"
            }, out var validated);
            if (validated is not JwtSecurityToken jwt || !FixedEquals(jwt.Claims.FirstOrDefault(item => item.Type == "nonce")?.Value, nonce)) return null;
            // OIDC Core 3.1.3.7: with several audiences, the token must be authorized for this client.
            if (jwt.Audiences.Count() > 1 && !string.Equals(jwt.Claims.FirstOrDefault(item => item.Type == "azp")?.Value, provider.ClientId, StringComparison.Ordinal)) return null;
            return principal;
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsStateValue(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200;
}
