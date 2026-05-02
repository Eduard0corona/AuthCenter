using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

public class ApplicationRegistrationSettings
{
    public Guid Id { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public ApplicationRegistrationMode RegistrationMode { get; set; }
    public Guid? DefaultRoleId { get; set; }
    public bool RequireEmailConfirmation { get; set; }
    public bool AllowGoogleLogin { get; set; }
    public bool AllowPasswordLogin { get; set; }
    public string? AllowedEmailDomains { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ApplicationSystem ApplicationSystem { get; set; } = null!;
}
