using AuthCenter.Domain.Common;
namespace AuthCenter.Domain.Entities;

/// <summary>
/// An API protected by AuthCenter (an RFC 8707 resource). Access tokens for it carry its identifier
/// as the audience and the roles and permissions its owning application grants the user.
/// </summary>
public class ApiResource : IVersionedEntity
{
    public long Version { get; set; }

    public Guid Id { get; set; }
    public Guid ApplicationSystemId { get; set; }

    /// <summary>The resource indicator and token audience: an absolute URI such as https://api.example.com/orders.</summary>
    public string Identifier { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ApplicationSystem ApplicationSystem { get; set; } = null!;
    public ICollection<ApiScope> Scopes { get; set; } = new List<ApiScope>();
}
