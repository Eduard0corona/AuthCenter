namespace AuthCenter.Contracts.Requests.Applications;

public class CreateApplicationRequest
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string RegistrationMode { get; init; } = "Open";

    /// <summary>
    /// "Employees" or "Consumers": the hosted login's wording. When omitted, open registration means
    /// consumers and any other mode employees.
    /// </summary>
    public string? Audience { get; init; }
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
