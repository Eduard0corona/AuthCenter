using System.Text.Json.Serialization;

namespace AuthCenter.Contracts.Responses.OAuth;

public class OAuthUserInfoResponse
{
    [JsonPropertyName("sub")]
    public string Sub { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; init; }

    [JsonPropertyName("email_verified")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EmailVerified { get; init; }
}
