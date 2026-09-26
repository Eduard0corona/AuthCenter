namespace AuthCenter.Application.Models;

/// <summary>An OpenID Connect RP-Initiated Logout 1.0 request.</summary>
public sealed class EndSessionRequest
{
    public string? IdTokenHint { get; init; }
    public string? ClientId { get; init; }
    public string? PostLogoutRedirectUri { get; init; }
    public string? State { get; init; }
}

/// <summary>The browser asking to sign out: its hosted-login session, if any, and its binding.</summary>
public sealed class EndSessionCaller
{
    public Guid? UserId { get; init; }
    public Guid? SessionId { get; init; }
    public string? BrowserBinding { get; init; }
}

/// <summary>
/// The outcome of a logout request: either the session was ended (or there was none) and the
/// browser goes to <see cref="RedirectUrl"/>, or the user must confirm on the hosted logout page.
/// </summary>
public sealed class EndSessionResult
{
    public string RedirectUrl { get; init; } = "/login";
    public bool SessionEnded { get; init; }
    public string? LogoutId { get; init; }
}

/// <summary>Non-sensitive details the hosted logout page shows while asking for confirmation.</summary>
public sealed class EndSessionContext
{
    public string? ClientDisplayName { get; init; }
    public string? ApplicationName { get; init; }
    public DateTime ExpiresAt { get; init; }
}
