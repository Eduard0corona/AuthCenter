using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

/// <summary>
/// OpenID Connect RP-Initiated Logout 1.0. A request carrying an ID token that belongs to the
/// browser's current session signs out at once; any other request must be confirmed by the user
/// on the hosted logout page. The post-logout redirect URI must be registered for the client.
/// </summary>
public sealed class EndSessionService : IEndSessionService
{
    private const string PendingPrefix = "oidc_logout";
    private const int PendingMinutes = 10;
    private const string SignedOutPage = "/login?signed_out=1";

    private readonly AuthCenterDbContext _db;
    private readonly ITokenService _tokens;
    private readonly ITransientStateStore _transientState;
    private readonly ISingleSignOnSessionService _sessions;
    private readonly IDateTimeProvider _clock;

    public EndSessionService(
        AuthCenterDbContext db,
        ITokenService tokens,
        ITransientStateStore transientState,
        ISingleSignOnSessionService sessions,
        IDateTimeProvider clock)
    {
        _db = db;
        _tokens = tokens;
        _transientState = transientState;
        _sessions = sessions;
        _clock = clock;
    }

    public async Task<OperationResult<EndSessionResult>> BeginAsync(EndSessionRequest request, EndSessionCaller caller, CancellationToken ct = default)
    {
        IdTokenHint? hint = null;
        if (!string.IsNullOrWhiteSpace(request.IdTokenHint))
        {
            hint = _tokens.ReadIdTokenHint(request.IdTokenHint);
            if (hint is null)
                return Failure("INVALID_REQUEST", "id_token_hint was not issued by this server.");
        }

        if (hint is not null && !string.IsNullOrWhiteSpace(request.ClientId) && !string.Equals(hint.ClientId, request.ClientId, StringComparison.Ordinal))
            return Failure("INVALID_REQUEST", "client_id does not match the id_token_hint audience.");

        var clientId = hint?.ClientId ?? (string.IsNullOrWhiteSpace(request.ClientId) ? null : request.ClientId);
        var client = clientId is null
            ? null
            : await _db.OAuthClients.AsNoTracking().Include(item => item.ApplicationSystem)
                .FirstOrDefaultAsync(item => item.ClientId == clientId && item.IsActive, ct);
        if (clientId is not null && client is null)
            return Failure("INVALID_CLIENT", "Unknown or inactive client.");

        var redirectUrl = SignedOutPage;
        if (!string.IsNullOrWhiteSpace(request.PostLogoutRedirectUri))
        {
            if (client is null)
                return Failure("INVALID_REQUEST", "post_logout_redirect_uri requires id_token_hint or client_id.");
            var registered = JsonSerializer.Deserialize<List<string>>(client.PostLogoutRedirectUrisJson) ?? [];
            if (!registered.Contains(request.PostLogoutRedirectUri, StringComparer.Ordinal))
                return Failure("INVALID_REQUEST", "post_logout_redirect_uri is not registered for this client.");
            redirectUrl = string.IsNullOrEmpty(request.State)
                ? request.PostLogoutRedirectUri
                : $"{request.PostLogoutRedirectUri}{(request.PostLogoutRedirectUri.Contains('?') ? '&' : '?')}state={Uri.EscapeDataString(request.State)}";
        }

        if (!await HasActiveSessionAsync(caller, ct))
            return OperationResult<EndSessionResult>.Success(new EndSessionResult { RedirectUrl = redirectUrl });

        // The ID token must belong to the signed-in user and, when it names one, to this session;
        // otherwise a third party could sign the user out, so the user is asked first.
        var hintMatchesSession = hint is not null &&
            string.Equals(hint.Subject, caller.UserId!.Value.ToString(), StringComparison.OrdinalIgnoreCase) &&
            (hint.SessionId is null || hint.SessionId == caller.SessionId);
        if (hintMatchesSession)
        {
            await _sessions.EndSessionAsync(caller.UserId!.Value, caller.SessionId!.Value, "rp_initiated_logout", ct);
            return OperationResult<EndSessionResult>.Success(new EndSessionResult { RedirectUrl = redirectUrl, SessionEnded = true });
        }

        var logoutId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var pending = new PendingLogout(client?.ClientId, client?.DisplayName, client?.ApplicationSystem.Name, redirectUrl, HashBinding(caller.BrowserBinding), _clock.UtcNow);
        await _transientState.SetAsync(PendingPrefix, logoutId, JsonSerializer.Serialize(pending), _clock.UtcNow.AddMinutes(PendingMinutes), ct);
        return OperationResult<EndSessionResult>.Success(new EndSessionResult { RedirectUrl = $"/logout?logout_id={logoutId}", LogoutId = logoutId });
    }

    public async Task<OperationResult<EndSessionContext>> GetPendingAsync(string logoutId, string? browserBinding, CancellationToken ct = default)
    {
        var pending = Read(await _transientState.GetAsync(PendingPrefix, logoutId, ct));
        if (pending is null || !BindingMatches(pending.BindingHash, browserBinding))
            return OperationResult<EndSessionContext>.Failure("INVALID_LOGOUT_REQUEST", "The sign-out request expired or was opened in another browser.");
        return OperationResult<EndSessionContext>.Success(new EndSessionContext
        {
            ClientDisplayName = pending.ClientDisplayName,
            ApplicationName = pending.ApplicationName,
            ExpiresAt = pending.CreatedAt.AddMinutes(PendingMinutes)
        });
    }

    public async Task<OperationResult<EndSessionResult>> ConfirmAsync(string logoutId, EndSessionCaller caller, CancellationToken ct = default)
    {
        var stored = await _transientState.GetAsync(PendingPrefix, logoutId, ct);
        var pending = Read(stored);
        if (pending is null || !BindingMatches(pending.BindingHash, caller.BrowserBinding))
            return Failure("INVALID_LOGOUT_REQUEST", "The sign-out request expired or was opened in another browser.");
        if (await _transientState.TakeAsync(PendingPrefix, logoutId, ct) is null)
            return Failure("INVALID_LOGOUT_REQUEST", "The sign-out request expired or was already used.");

        var ended = false;
        if (await HasActiveSessionAsync(caller, ct))
            ended = (await _sessions.EndSessionAsync(caller.UserId!.Value, caller.SessionId!.Value, "rp_initiated_logout", ct)).IsSuccess;
        return OperationResult<EndSessionResult>.Success(new EndSessionResult { RedirectUrl = pending.RedirectUrl, SessionEnded = ended });
    }

    private async Task<bool> HasActiveSessionAsync(EndSessionCaller caller, CancellationToken ct)
    {
        if (caller.UserId is not { } userId || caller.SessionId is not { } sessionId)
            return false;
        var now = _clock.UtcNow;
        return await _db.RefreshTokens.AsNoTracking().AnyAsync(
            token => token.Id == sessionId && token.UserId == userId && token.OAuthClientId == null && token.RevokedAt == null && token.ExpiresAt > now,
            ct);
    }

    private static PendingLogout? Read(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try { return JsonSerializer.Deserialize<PendingLogout>(value); }
        catch (JsonException) { return null; }
    }

    private static string? HashBinding(string? binding) =>
        string.IsNullOrWhiteSpace(binding) ? null : Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(binding)));

    private static bool BindingMatches(string? storedHash, string? binding) =>
        storedHash is null ||
        (HashBinding(binding) is { } presented &&
         CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(storedHash), Encoding.UTF8.GetBytes(presented)));

    private static OperationResult<EndSessionResult> Failure(string code, string message) =>
        OperationResult<EndSessionResult>.Failure(code, message);

    private sealed record PendingLogout(
        string? ClientId,
        string? ClientDisplayName,
        string? ApplicationName,
        string RedirectUrl,
        string? BindingHash,
        DateTime CreatedAt);
}
