namespace AuthCenter.Application.Models;

/// <summary>
/// An authorization response for the client's registered redirect URI, delivered either in the
/// query string or as an auto-submitted form (OAuth 2.0 Form Post Response Mode).
/// </summary>
public sealed class AuthorizationResponse
{
    public const string Query = "query";
    public const string FormPost = "form_post";

    public string RedirectUri { get; init; } = string.Empty;
    public string ResponseMode { get; init; } = Query;
    public IReadOnlyList<KeyValuePair<string, string>> Parameters { get; init; } = [];

    public bool IsFormPost => string.Equals(ResponseMode, FormPost, StringComparison.Ordinal);

    public string ToRedirectUrl()
    {
        var separator = RedirectUri.Contains('?') ? '&' : '?';
        return RedirectUri + separator + string.Join("&", Parameters.Select(parameter =>
            $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
    }
}

/// <summary>What the authorization endpoint does next: send the browser to the login page or answer the client.</summary>
public sealed class AuthorizationEndpointResult
{
    public string? LoginUrl { get; init; }
    public AuthorizationResponse? Response { get; init; }
}

/// <summary>
/// Who is calling the authorization endpoints: the single sign-on session the browser already
/// holds (if any) and the browser binding cookie that ties an interaction to that browser.
/// </summary>
public sealed class AuthorizationCaller
{
    public Guid? UserId { get; init; }
    public Guid? SessionId { get; init; }
    public string? BrowserBinding { get; init; }

    /// <summary>The browser's address and user agent, for access policies and risk signals.</summary>
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }

    /// <summary>True when authenticated with the hosted-login cookie; the interaction must then belong to this browser.</summary>
    public bool RequiresBrowserBinding { get; init; }
}
