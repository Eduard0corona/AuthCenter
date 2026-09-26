using System.Globalization;
using System.Text.RegularExpressions;

namespace AuthCenter.Infrastructure.Services;

/// <summary>Normalises the email domains used for home realm discovery.</summary>
internal static partial class FederationDomains
{
    private static readonly IdnMapping Idn = new();

    /// <summary>The lower-case ASCII (punycode) domain name, or <c>null</c> when the value is not one.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300)
            return null;
        var domain = value.Trim().TrimStart('@').TrimEnd('.');
        try { domain = Idn.GetAscii(domain).ToLowerInvariant(); }
        catch (ArgumentException) { return null; }
        return DomainPattern().IsMatch(domain) ? domain : null;
    }

    public static string? OfEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;
        var trimmed = email.Trim();
        var at = trimmed.LastIndexOf('@');
        return at <= 0 || at == trimmed.Length - 1 ? null : Normalize(trimmed[(at + 1)..]);
    }

    [GeneratedRegex("^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();
}
