namespace AuthCenter.Domain.Entities;

public class ExternalIdentityProvider
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderUserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? PictureUrl { get; set; }
    public DateTime LinkedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public bool IsActive { get; set; } = true;

    public ApplicationUser User { get; set; } = null!;
}
