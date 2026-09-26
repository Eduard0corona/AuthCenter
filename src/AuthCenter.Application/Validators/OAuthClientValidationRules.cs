namespace AuthCenter.Application.Validators;

internal static class OAuthClientValidationRules
{
    internal static bool IsSecureBrowserUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
               (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An origin: scheme://host[:port] with nothing after it, HTTPS or HTTP loopback.</summary>
    internal static bool IsSecureOrigin(string value) =>
        IsSecureBrowserUri(value) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(value.TrimEnd('/'), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    internal static bool HasUniqueValues(IEnumerable<string> values) =>
        values.Distinct(StringComparer.Ordinal).Count() == values.Count();
}
