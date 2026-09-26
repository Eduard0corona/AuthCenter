using System.Security.Cryptography;
using System.Text;
using AuthCenter.Api.Authorization;
using AuthCenter.Contracts.Responses;

namespace AuthCenter.Api.Middleware;

public sealed class UiCsrfMiddleware(RequestDelegate next)
{
    public const string CookieName = "__Host-AuthCenter.Csrf";
    public const string HeaderName = "X-AuthCenter-CSRF";

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsUnsafe(context.Request.Method) &&
            string.Equals(context.User.Identity?.AuthenticationType, AuthenticationSchemes.UiCookie, StringComparison.Ordinal) &&
            context.GetEndpoint()?.Metadata.GetMetadata<IgnoreUiCsrfAttribute>() is null)
        {
            var cookie = context.Request.Cookies[CookieName];
            var header = context.Request.Headers[HeaderName].ToString();
            if (!FixedTimeEquals(cookie, header))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(ApiResponse.Fail("INVALID_CSRF_TOKEN", "A valid same-origin CSRF token is required."));
                return;
            }
        }

        await next(context);
    }

    public static string IssueToken(HttpResponse response)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            MaxAge = TimeSpan.FromMinutes(30)
        });
        return token;
    }

    private static bool IsUnsafe(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    private static bool FixedTimeEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

/// <summary>
/// Marks an endpoint that never acts with the hosted-login cookie and is protected otherwise (for
/// example the SAML ACS, which a same-site identity provider posts with the cookie attached).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class IgnoreUiCsrfAttribute : Attribute;
