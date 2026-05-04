namespace AuthCenter.Contracts.Responses.OAuth;

public class OAuthClientCreatedResponse
{
    public OAuthClientResponse Client { get; init; } = null!;
    public string? ClientSecret { get; init; }
}
