namespace AuthCenter.Contracts.Responses.Auth;

/// <summary>What the hosted login offers for a direct sign-in to an application.</summary>
public sealed class LoginOptionsResponse
{
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public bool AllowPasswordLogin { get; init; }
    public bool AllowMagicLink { get; init; }
    public bool FederationAvailable { get; init; }
}
