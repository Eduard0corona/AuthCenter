using System.Text.Json.Serialization;

namespace AuthCenter.Contracts.Responses.OAuth;

/// <summary>RFC 7662 introspection response. An inactive token reveals nothing else.</summary>
public class OAuthIntrospectionResponse
{
    public static OAuthIntrospectionResponse Inactive { get; } = new() { Active = false };

    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }

    [JsonPropertyName("client_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientId { get; init; }

    [JsonPropertyName("token_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenType { get; init; }

    [JsonPropertyName("exp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ExpiresAt { get; init; }

    [JsonPropertyName("iat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? IssuedAt { get; init; }

    [JsonPropertyName("sub")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subject { get; init; }

    [JsonPropertyName("aud")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Audience { get; init; }

    [JsonPropertyName("iss")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Issuer { get; init; }

    [JsonPropertyName("jti")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenId { get; init; }
}
