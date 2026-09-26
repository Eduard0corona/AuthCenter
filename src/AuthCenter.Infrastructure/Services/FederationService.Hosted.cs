using System.Security.Cryptography;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Federation;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Federation;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

/// <summary>
/// Federation from the hosted login. The browser that starts a federated sign-in is bound to it:
/// the upstream callback only records a single-use result, and the hosted login redeems it from
/// that same browser, where the application's access policy and MFA gate run before a session
/// exists. A callback delivered to another browser (login CSRF) therefore signs nobody in.
/// </summary>
public sealed partial class FederationService
{
    private const string ResultPurpose = "federation_result";
    private const string HostedLoginPath = "/login";
    private static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(5);

    public async Task<OperationResult<FederationDiscoveryResponse>> DiscoverAsync(FederationDiscoveryRequest request, FederationCaller caller, CancellationToken ct = default)
    {
        var target = await ResolveTargetAsync(request.InteractionId, request.ApplicationCode, caller.BrowserBinding, ct);
        if (target is null)
            return OperationResult<FederationDiscoveryResponse>.Failure("INVALID_INTERACTION", "The sign-in request expired or was started in another browser.");
        var domain = !string.IsNullOrWhiteSpace(request.Email) ? FederationDomains.OfEmail(request.Email) : FederationDomains.Normalize(request.Domain);
        if (domain is null)
            return OperationResult<FederationDiscoveryResponse>.Success(new FederationDiscoveryResponse { Federated = false });

        // Group and profile conditions would disclose directory data about any email, so they only
        // apply to the user this browser is already signed in as (for example a re-authentication).
        Guid? identified = null;
        if (caller.SignedInUserId is { } signedIn && !string.IsNullOrWhiteSpace(request.Email))
        {
            var normalizedEmail = request.Email.Trim().ToUpperInvariant();
            if (await _db.Users.AsNoTracking().AnyAsync(user => user.Id == signedIn && user.NormalizedEmail == normalizedEmail, ct))
                identified = signedIn;
        }

        var rule = await MatchRuleAsync(target.ApplicationSystemId, domain, identified, evaluateDirectoryConditions: identified is not null, ct);
        return OperationResult<FederationDiscoveryResponse>.Success(new FederationDiscoveryResponse
        {
            Federated = rule is not null,
            Provider = rule is null ? null : new FederationProviderSummary
            {
                Id = rule.FederationProviderId,
                Name = rule.FederationProvider.Name,
                Protocol = rule.FederationProvider.Protocol.ToString()
            }
        });
    }

    public async Task<OperationResult<StartFederationResponse>> StartAsync(StartFederationRequest request, FederationCaller caller, CancellationToken ct = default)
    {
        var bindingHash = HashBinding(caller.BrowserBinding);
        if (bindingHash is null)
            return OperationResult<StartFederationResponse>.Failure("INTERACTION_BINDING_MISMATCH", "This browser cannot start a federated sign-in.");
        var target = await ResolveTargetAsync(request.InteractionId, request.ApplicationCode, caller.BrowserBinding, ct);
        if (target is null)
            return OperationResult<StartFederationResponse>.Failure("INVALID_INTERACTION", "The sign-in request expired or was started in another browser.");
        var provider = await _db.FederationProviders.AsNoTracking().Include(item => item.ApplicationSystem)
            .FirstOrDefaultAsync(item => item.Id == request.ProviderId && item.IsActive && item.ApplicationSystemId == target.ApplicationSystemId, ct);
        if (provider is null)
            return OperationResult<StartFederationResponse>.Failure("FEDERATION_PROVIDER_NOT_FOUND", "The identity provider is not available for this application.");

        var transaction = new FederationTransaction
        {
            ProviderId = provider.Id,
            Hosted = true,
            BindingHash = bindingHash,
            InteractionId = target.InteractionId,
            ApplicationCode = target.ApplicationCode,
            ReturnUrl = target.InteractionId is null ? LocalReturnUrl(request.ReturnUrl) : null
        };
        var loginHint = string.IsNullOrWhiteSpace(request.LoginHint) ? target.LoginHint : request.LoginHint.Trim();
        OperationResult<string> redirect;
        if (provider.Protocol == FederationProtocol.Oidc)
        {
            redirect = IsHostedCallback(provider.OidcCallbackUrl)
                ? await BuildOidcChallengeAsync(provider, transaction, loginHint, target.ForceAuthentication, ct)
                : OperationResult<string>.Failure("FEDERATION_CALLBACK_NOT_HOSTED", $"Register {HostedOidcCallbackUrl ?? HostedOidcCallbackPath} as the provider's callback URL to use it from the hosted login.");
        }
        else
        {
            redirect = await BuildSamlChallengeAsync(provider, transaction, target.ForceAuthentication, ct);
        }
        if (!redirect.IsSuccess)
            return OperationResult<StartFederationResponse>.Failure(redirect.ErrorCode!, redirect.Message!);

        await _audit.LogAsync("FEDERATION_LOGIN_STARTED", null, provider.ApplicationSystem.Code, nameof(FederationProvider), provider.Id.ToString(), caller.IpAddress, caller.UserAgent,
            new { protocol = provider.Protocol.ToString(), authorizationRequest = target.InteractionId is not null }, ct);
        return OperationResult<StartFederationResponse>.Success(new StartFederationResponse
        {
            RedirectUrl = redirect.Data!,
            ExpiresIn = (int)UpstreamLifetime.TotalSeconds
        });
    }

    public async Task<string> CompleteOidcCallbackAsync(string? state, string? code, string? error, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var transaction = IsStateValue(state) ? ReadTransaction(await _state.TakeAsync(OidcStatePurpose, state!, ct)) : null;
        if (transaction is not { Hosted: true })
            return LoginErrorPath("FEDERATION_STATE_INVALID");

        OperationResult<FederatedAuthentication> outcome;
        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
        {
            var cancelled = string.Equals(error, "access_denied", StringComparison.Ordinal);
            await _audit.LogAsync("FEDERATION_LOGIN_FAILED", null, transaction.ApplicationCode, nameof(FederationProvider), transaction.ProviderId.ToString(), ipAddress, userAgent,
                new { protocol = "OIDC", reason = cancelled ? "UpstreamAccessDenied" : "UpstreamError", upstreamError = SanitizeProtocolCode(error) }, ct);
            outcome = cancelled
                ? OperationResult<FederatedAuthentication>.Failure("FEDERATION_CANCELLED", "The sign-in was cancelled at the identity provider.")
                : OperationResult<FederatedAuthentication>.Failure("FEDERATION_UPSTREAM_ERROR", "The identity provider could not complete the sign-in.");
        }
        else
        {
            outcome = await AuthenticateOidcAsync(transaction, code, ipAddress, userAgent, ct);
        }
        return await StoreResultAsync(transaction, outcome, ct);
    }

    public async Task<OperationResult<AuthResponse>> RedeemResultAsync(string handle, FederationCaller caller, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(handle) || handle.Length > 100)
            return OperationResult<AuthResponse>.Failure("FEDERATION_RESULT_INVALID", "The sign-in result expired or was already used.");
        var result = ReadResult(await _state.GetAsync(ResultPurpose, handle, ct));
        if (result is null)
            return OperationResult<AuthResponse>.Failure("FEDERATION_RESULT_INVALID", "The sign-in result expired or was already used.");
        if (!FixedEquals(result.BindingHash, HashBinding(caller.BrowserBinding)))
        {
            await _audit.LogAsync("FEDERATION_BINDING_MISMATCH", result.UserId, result.ApplicationCode, ipAddress: caller.IpAddress, userAgent: caller.UserAgent, ct: ct);
            return OperationResult<AuthResponse>.Failure("INTERACTION_BINDING_MISMATCH", "This sign-in was started in a different browser.");
        }
        if (await _state.TakeAsync(ResultPurpose, handle, ct) is null)
            return OperationResult<AuthResponse>.Failure("FEDERATION_RESULT_INVALID", "The sign-in result expired or was already used.");
        if (result.ErrorCode is not null || result.UserId is null || result.ApplicationCode is null)
            return OperationResult<AuthResponse>.Failure(result.ErrorCode ?? "FEDERATION_RESULT_INVALID", result.ErrorMessage ?? "The federated sign-in failed.");

        return await _auth.CompleteFederatedSignInAsync(
            result.UserId.Value,
            result.ApplicationCode,
            new AuthenticationContext(result.Methods, result.Assurance),
            caller.IpAddress,
            caller.UserAgent,
            ct);
    }

    /// <summary>Common ending of both protocols once the upstream identity is known.</summary>
    private async Task<OperationResult<FederatedAuthentication>> AuthenticateUpstreamIdentityAsync(FederationProvider provider, UpstreamIdentity identity, string protocol, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        var user = await ResolveFederatedUserAsync(provider, identity, ct);
        if (!user.IsSuccess)
            return await FailAsync(provider, user.ErrorCode!, user.Message!, protocol, ipAddress, userAgent, ct);

        // A provider trusted for MFA turns an upstream multi-factor sign-in into AuthCenter's MFA level.
        var trustedMfa = provider.TrustUpstreamMfa && identity.MultiFactor;
        var authentication = trustedMfa
            ? new AuthenticationContext([DomainConstants.AuthenticationMethods.Federated, DomainConstants.AuthenticationMethods.MultiFactor], AuthenticationAssuranceLevel.Mfa)
            : AuthenticationContext.Federated;
        await _audit.LogAsync("FEDERATION_LOGIN_SUCCESS", user.Data!.Id, provider.ApplicationSystem.Code, nameof(FederationProvider), provider.Id.ToString(), ipAddress, userAgent,
            new { protocol, upstreamMfa = identity.MultiFactor, trustedMfa }, ct);
        return OperationResult<FederatedAuthentication>.Success(new FederatedAuthentication(user.Data, provider, authentication));
    }

    private async Task<OperationResult<FederatedAuthentication>> FailAsync(FederationProvider provider, string code, string message, string protocol, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        await _audit.LogAsync("FEDERATION_LOGIN_FAILED", null, provider.ApplicationSystem?.Code, nameof(FederationProvider), provider.Id.ToString(), ipAddress, userAgent,
            new { protocol, reason = code }, ct);
        return OperationResult<FederatedAuthentication>.Failure(code, message);
    }

    /// <summary>Records the outcome for the starting browser and returns where the hosted login continues.</summary>
    private async Task<string> StoreResultAsync(FederationTransaction transaction, OperationResult<FederatedAuthentication> outcome, CancellationToken ct)
    {
        var result = outcome.IsSuccess
            ? new FederationResult
            {
                BindingHash = transaction.BindingHash,
                UserId = outcome.Data!.User.Id,
                ApplicationCode = outcome.Data.Provider.ApplicationSystem.Code,
                Methods = outcome.Data.Authentication.Methods.ToArray(),
                Assurance = outcome.Data.Authentication.Assurance
            }
            : new FederationResult
            {
                BindingHash = transaction.BindingHash,
                ApplicationCode = transaction.ApplicationCode,
                ErrorCode = outcome.ErrorCode,
                ErrorMessage = outcome.Message
            };
        var handle = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await _state.SetAsync(ResultPurpose, handle, JsonSerializer.Serialize(result), _clock.UtcNow.Add(ResultLifetime), ct);

        var query = new Dictionary<string, string?>();
        if (transaction.InteractionId is not null)
        {
            query["interaction_id"] = transaction.InteractionId;
        }
        else
        {
            query["application"] = transaction.ApplicationCode;
            query["return_url"] = transaction.ReturnUrl;
        }
        query["federation_result"] = handle;
        return QueryHelpers.AddQueryString(HostedLoginPath, query);
    }

    private async Task<HostedTarget?> ResolveTargetAsync(string? interactionId, string? applicationCode, string? browserBinding, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(interactionId))
        {
            var interaction = await _oauth.GetInteractionTargetAsync(interactionId, browserBinding, ct);
            return interaction is null
                ? null
                : new HostedTarget(interaction.ApplicationSystemId, interaction.ApplicationCode, interactionId, interaction.LoginHint, interaction.ForceAuthentication);
        }
        if (string.IsNullOrWhiteSpace(applicationCode))
            return null;
        var code = applicationCode.Trim();
        var application = await _db.ApplicationSystems.AsNoTracking()
            .Where(item => item.Code == code && item.IsActive)
            .Select(item => new { item.Id, item.Code })
            .FirstOrDefaultAsync(ct);
        return application is null ? null : new HostedTarget(application.Id, application.Code, null, null, false);
    }

    /// <summary>The OIDC callback is AuthCenter's own server-side endpoint (the hosted login cannot use an application's callback).</summary>
    private bool IsHostedCallback(string? callback)
    {
        if (!Uri.TryCreate(callback, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.Query))
            return false;
        return HostedOidcCallbackUrl is { } expected
            ? string.Equals(uri.GetLeftPart(UriPartial.Path), expected, StringComparison.OrdinalIgnoreCase)
            : uri.AbsolutePath.EndsWith(HostedOidcCallbackPath, StringComparison.Ordinal);
    }

    private static string LoginErrorPath(string code) =>
        QueryHelpers.AddQueryString(HostedLoginPath, "federation_error", code);

    /// <summary>A local path to return to after a direct (non-OAuth) hosted sign-in, never another origin.</summary>
    private static string? LocalReturnUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 2000 || !trimmed.StartsWith('/') || trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.Contains('\\', StringComparison.Ordinal) || trimmed.Any(char.IsControl))
            return null;
        return trimmed;
    }

    private static string? SanitizeProtocolCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new string(value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.').Take(64).ToArray());

    private static FederationTransaction? ReadTransaction(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<FederationTransaction>(json); }
        catch (JsonException) { return null; }
    }

    private static FederationResult? ReadResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<FederationResult>(json); }
        catch (JsonException) { return null; }
    }

    private sealed record HostedTarget(Guid ApplicationSystemId, string ApplicationCode, string? InteractionId, string? LoginHint, bool ForceAuthentication);

    private sealed record FederatedAuthentication(ApplicationUser User, FederationProvider Provider, AuthenticationContext Authentication);

    /// <summary>An upstream sign-in in progress, stored under the OIDC state or the SAML RelayState.</summary>
    private sealed record FederationTransaction
    {
        public Guid ProviderId { get; init; }

        /// <summary>Started by the hosted login (browser-bound) rather than the JSON API.</summary>
        public bool Hosted { get; init; }
        public string? BindingHash { get; init; }
        public string? InteractionId { get; init; }
        public string? ApplicationCode { get; init; }
        public string? ReturnUrl { get; init; }
        public string? ApiInteractionId { get; init; }
        public string? Nonce { get; init; }
        public string? Verifier { get; init; }
        public string? RequestId { get; init; }
    }

    /// <summary>What the upstream callback produced, redeemable once by the starting browser.</summary>
    private sealed record FederationResult
    {
        public string? BindingHash { get; init; }
        public Guid? UserId { get; init; }
        public string? ApplicationCode { get; init; }
        public string[] Methods { get; init; } = [];
        public AuthenticationAssuranceLevel Assurance { get; init; } = AuthenticationAssuranceLevel.Password;
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
