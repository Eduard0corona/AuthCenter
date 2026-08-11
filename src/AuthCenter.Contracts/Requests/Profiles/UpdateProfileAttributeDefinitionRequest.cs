using System.Text.Json;

namespace AuthCenter.Contracts.Requests.Profiles;

public sealed class UpdateProfileAttributeDefinitionRequest
{
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string DataType { get; init; } = "String";
    public bool IsRequired { get; init; }
    public bool IsActive { get; init; } = true;
    public JsonElement? DefaultValue { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public decimal? MinimumNumber { get; init; }
    public decimal? MaximumNumber { get; init; }
    public string? ValidationPattern { get; init; }
    public IReadOnlyList<JsonElement> AllowedValues { get; init; } = [];
}
