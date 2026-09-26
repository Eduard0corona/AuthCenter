namespace AuthCenter.Api.Extensions;

/// <summary>
/// Hosted pages that the links in AuthCenter's emails open when <c>ActionLinks</c> points at
/// AuthCenter itself (the default paths of <c>ActionLinkSettings</c>).
/// </summary>
public static class HostedPages
{
    public const string MagicLinkPath = "/magic-link";

    public static readonly string[] ActionPaths =
    [
        "/reset-password", "/accept-invitation", "/confirm-email", "/confirm-email-change", MagicLinkPath
    ];
}
