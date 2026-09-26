namespace AuthCenter.Infrastructure.Settings;

/// <summary>The public origin AuthCenter is reached at (<c>Oidc:PublicOrigin</c>).</summary>
public sealed class OidcSettings
{
    public string PublicOrigin { get; init; } = string.Empty;

    /// <summary>The configured origin without a trailing slash, or <c>null</c> when it is not an absolute URL.</summary>
    public string? NormalizedPublicOrigin =>
        Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) && !PublicOrigin.Contains("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase)
            ? origin.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : null;
}
