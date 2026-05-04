using System.Text.Json.Serialization;

namespace AuthCenter.Contracts.Responses.OAuth;

public class OAuthUserInfoResponse
{
    [JsonPropertyName("sub")]
    public string Sub { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("email_verified")]
    public bool? EmailVerified { get; init; }
}
