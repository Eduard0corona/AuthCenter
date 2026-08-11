namespace AuthCenter.Client;

public sealed class AuthCenterClientOptions
{
    public required Uri Authority { get; init; }
    public required string ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = ["openid", "profile", "email"];
}
