using AuthCenter.Domain.Common;

namespace AuthCenter.Domain.Entities;

/// <summary>
/// Two roles no user may hold at once, whichever applications they belong to and however they are
/// held (directly or through groups). Administrative changes that would combine them are refused;
/// combinations that come from elsewhere (SCIM, group rules, federation) are reported.
/// </summary>
public class SeparationOfDutiesRule : IVersionedEntity
{
    public long Version { get; set; }

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid FirstRoleId { get; set; }
    public Guid SecondRoleId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ApplicationRole FirstRole { get; set; } = null!;
    public ApplicationRole SecondRole { get; set; } = null!;
}
