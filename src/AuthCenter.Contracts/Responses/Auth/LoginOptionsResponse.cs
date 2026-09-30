namespace AuthCenter.Contracts.Responses.Auth;

/// <summary>What the hosted login offers for a direct sign-in to an application.</summary>
public sealed class LoginOptionsResponse
{
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public bool AllowPasswordLogin { get; init; }
    public bool AllowMagicLink { get; init; }
    public bool FederationAvailable { get; init; }
    /// <summary>The hosted login offers to create an account (open or approval-based registration).</summary>
    public bool AllowSelfRegistration { get; init; }
}
