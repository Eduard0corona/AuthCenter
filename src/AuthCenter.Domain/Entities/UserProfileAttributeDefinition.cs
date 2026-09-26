using AuthCenter.Domain.Common;
using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

public class UserProfileAttributeDefinition : IVersionedEntity
{
    public long Version { get; set; }

    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ProfileAttributeDataType DataType { get; set; }
    public bool IsRequired { get; set; }
    public bool IsActive { get; set; } = true;
    public string? DefaultValueJson { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public decimal? MinimumNumber { get; set; }
    public decimal? MaximumNumber { get; set; }
    public string? ValidationPattern { get; set; }
    public string? AllowedValuesJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ICollection<UserProfileAttributeValue> Values { get; set; } = new List<UserProfileAttributeValue>();
}
