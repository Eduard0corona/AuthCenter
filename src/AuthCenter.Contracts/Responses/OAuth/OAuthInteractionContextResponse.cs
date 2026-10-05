namespace AuthCenter.Contracts.Responses.OAuth;

/// <summary>
/// Non-sensitive context the hosted login needs before the user signs in: which application the
/// authorization request is for, the login hint and whether a fresh sign-in is mandatory.
/// </summary>
public class OAuthInteractionContextResponse
{
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public string ClientDisplayName { get; init; } = string.Empty;
    public string? LoginHint { get; init; }
    public bool RequiresFreshLogin { get; init; }
    public bool AllowPasswordLogin { get; init; }
    public bool AllowMagicLink { get; init; }

    /// <summary>The application has active federation providers, so the login offers home realm discovery.</summary>
    public bool FederationAvailable { get; init; }
    /// <summary>The hosted login offers to create an account (open or approval-based registration).</summary>
    public bool AllowSelfRegistration { get; init; }
    /// <summary>"Employees" or "Consumers": who signs in, which sets the login's wording.</summary>
    public string Audience { get; init; } = string.Empty;

    /// <summary>Provider requested with <c>idp</c>: the hosted login redirects to it directly.</summary>
    public Federation.FederationProviderSummary? IdentityProvider { get; init; }
    public string? DomainHint { get; init; }
    public DateTime ExpiresAt { get; init; }
}
