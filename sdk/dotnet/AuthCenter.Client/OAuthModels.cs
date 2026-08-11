using System.Text.Json.Serialization;

namespace AuthCenter.Client;

public sealed class OAuthTokenSet
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = string.Empty;
    [JsonPropertyName("token_type")] public string TokenType { get; init; } = "Bearer";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    [JsonPropertyName("id_token")] public string? IdToken { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
}

public sealed record PkcePair(string Verifier, string Challenge);
