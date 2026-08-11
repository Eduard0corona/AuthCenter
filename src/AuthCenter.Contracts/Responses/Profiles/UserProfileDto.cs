using System.Text.Json;

namespace AuthCenter.Contracts.Responses.Profiles;

public sealed class UserProfileDto
{
    public Guid UserId { get; init; }
    public bool IsValid { get; init; }
    public IReadOnlyList<string> MissingRequiredAttributes { get; init; } = [];
    public IReadOnlyList<UserProfileAttributeValueDto> Attributes { get; init; } = [];
}

public sealed class UserProfileAttributeValueDto
{
    public string Key { get; init; } = string.Empty;
    public JsonElement Value { get; init; }
    public bool IsDefault { get; init; }
}
