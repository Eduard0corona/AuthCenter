namespace AuthCenter.Contracts.Responses.Auth;

public class ExternalProviderDto
{
    public Guid Id { get; init; }
    public string Provider { get; init; } = string.Empty;

    /// <summary>Display name: the enterprise provider's name, or the social provider itself.</summary>
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>True for an enterprise (OIDC/SAML) provider configured in AuthCenter.</summary>
    public bool Enterprise { get; init; }
    public string? Email { get; init; }
    public string? DisplayName { get; init; }
    public string? PictureUrl { get; init; }
    public DateTime LinkedAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
}
