using System.Text.Json;

namespace AuthCenter.Contracts.Responses.Profiles;

public sealed class ProfileAttributeDefinitionDto
{
    public Guid Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string DataType { get; init; } = string.Empty;
    public bool IsRequired { get; init; }
    public bool IsActive { get; init; }
    public JsonElement? DefaultValue { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public decimal? MinimumNumber { get; init; }
    public decimal? MaximumNumber { get; init; }
    public string? ValidationPattern { get; init; }
    public IReadOnlyList<JsonElement> AllowedValues { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}
