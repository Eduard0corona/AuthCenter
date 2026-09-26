using System.Security.Cryptography;

namespace AuthCenter.Api.Authorization;

/// <summary>
/// A random, HttpOnly per-browser value. Authorization interactions store its hash, so a
/// sign-in started in one browser cannot be completed by the session cookie of another.
/// </summary>
public static class BrowserBinding
{
    public const string CookieName = "__Host-AuthCenter.Browser";

    public static string? Read(HttpContext context)
    {
        var value = context.Request.Cookies[CookieName];
        return value is { Length: >= 32 and <= 64 } ? value : null;
    }

    public static string Ensure(HttpContext context)
    {
        if (Read(context) is { } existing)
            return existing;

        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        context.Response.Cookies.Append(CookieName, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true
        });
        return value;
    }
}
