using Microsoft.AspNetCore.Identity;

namespace AuthCenter.Domain.Entities;

public class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;
    public string? PictureUrl { get; set; }
    public bool IsExternalUser { get; set; }
    public bool HasLocalPassword { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public ICollection<ExternalIdentityProvider> ExternalIdentityProviders { get; set; } = new List<ExternalIdentityProvider>();
    public ICollection<UserApplicationAccess> ApplicationAccesses { get; set; } = new List<UserApplicationAccess>();
    public UserMfaCredential? MfaCredential { get; set; }
    public ICollection<UserGroupMembership> GroupMemberships { get; set; } = new List<UserGroupMembership>();
}
