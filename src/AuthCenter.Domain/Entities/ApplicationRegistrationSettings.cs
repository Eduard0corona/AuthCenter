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
    public bool AllowMicrosoftLogin { get; set; }
    public bool AllowGitHubLogin { get; set; }
    public bool AllowAppleLogin { get; set; }
    public bool AllowMagicLink { get; set; }
    public bool AllowPasswordLogin { get; set; }
    public bool RequireMfa { get; set; }
    public string? AllowedEmailDomains { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ApplicationSystem ApplicationSystem { get; set; } = null!;

    /// <summary>People may create their own password account from the hosted login.</summary>
    public static bool AllowsSelfRegistration(ApplicationRegistrationMode mode, bool allowPasswordLogin) =>
        allowPasswordLogin && mode is ApplicationRegistrationMode.Open or ApplicationRegistrationMode.ApprovalRequired;
}
