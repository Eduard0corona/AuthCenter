using AuthCenter.Domain.Constants;

namespace AuthCenter.Infrastructure.Services;

/// <summary>
/// What end users call an application: its branded name, else its name. End users only know the
/// applications they sign in to, so the system application (the identity service itself) has no
/// name until an administrator renames it in its branding; until then it is seeded as the product.
/// </summary>
internal static class EndUserNames
{
    private const string ProductName = "AuthCenter";

    public static string Of(string code, string name, string? brandedName)
    {
        var shown = (string.IsNullOrWhiteSpace(brandedName) ? name : brandedName).Trim();
        return string.Equals(code, DomainConstants.SystemCodes.AuthCenter, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(shown, ProductName, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : shown;
    }
}
