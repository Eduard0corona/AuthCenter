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
    public DateTime ExpiresAt { get; init; }
}
