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
    [JsonPropertyName("issued_token_type")] public string? IssuedTokenType { get; init; }
}

/// <summary>An RFC 7662 introspection response; an inactive token carries nothing else.</summary>
public sealed class AuthCenterIntrospectionResult
{
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("client_id")] public string? ClientId { get; init; }
    [JsonPropertyName("token_type")] public string? TokenType { get; init; }
    [JsonPropertyName("exp")] public long? ExpiresAt { get; init; }
    [JsonPropertyName("iat")] public long? IssuedAt { get; init; }
    [JsonPropertyName("sub")] public string? Subject { get; init; }
    [JsonPropertyName("aud")] public IReadOnlyList<string>? Audience { get; init; }
    [JsonPropertyName("iss")] public string? Issuer { get; init; }
    [JsonPropertyName("jti")] public string? TokenId { get; init; }
}

public sealed record PkcePair(string Verifier, string Challenge);
