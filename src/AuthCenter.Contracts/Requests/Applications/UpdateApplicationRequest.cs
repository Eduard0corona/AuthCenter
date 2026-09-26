namespace AuthCenter.Contracts.Requests.Applications;

public class UpdateApplicationRequest
{
    /// <summary>The version the caller loaded; when sent, a record changed since then is not overwritten (409).</summary>
    public long? Version { get; init; }

    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string RegistrationMode { get; init; } = "Open";
    public bool AllowGoogleLogin { get; init; }
    public bool AllowMicrosoftLogin { get; init; }
    public bool AllowGitHubLogin { get; init; }
    public bool AllowAppleLogin { get; init; }
    public bool AllowMagicLink { get; init; }
    public bool AllowPasswordLogin { get; init; } = true;
    public bool RequireEmailConfirmation { get; init; }
    public bool RequireMfa { get; init; }
    public string? AllowedEmailDomains { get; init; }
    public Guid? DefaultRoleId { get; init; }
}
