namespace AuthCenter.Infrastructure.Services;

/// <summary>
/// The home of an application, taken from an address registered for it (a redirect URI or an
/// assertion consumer service URL): where the hosted pages send the user back to it.
/// </summary>
internal static class ApplicationOrigin
{
    /// <summary>The address's origin with a trailing slash; null unless HTTPS or HTTP on loopback.</summary>
    public static string? From(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        var secure = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        return secure ? $"{uri.GetLeftPart(UriPartial.Authority)}/" : null;
    }
}
