using System.Text.Json;

namespace AuthCenter.Contracts.Responses.Auth;

public sealed class PasskeyOptionsResponse
{
    public string? InteractionId { get; init; }
    public JsonElement PublicKey { get; init; }
}
