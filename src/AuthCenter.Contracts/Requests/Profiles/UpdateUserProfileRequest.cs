using System.Text.Json;

namespace AuthCenter.Contracts.Requests.Profiles;

public sealed class UpdateUserProfileRequest
{
    public IReadOnlyDictionary<string, JsonElement?> Attributes { get; init; } =
        new Dictionary<string, JsonElement?>(StringComparer.OrdinalIgnoreCase);
}
