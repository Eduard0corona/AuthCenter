namespace AuthCenter.Contracts.Responses.Applications;

public class ApplicationDto
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public ApplicationRegistrationSettingsDto? RegistrationSettings { get; init; }
}

public class ApplicationRegistrationSettingsDto
{
    public string RegistrationMode { get; init; } = string.Empty;
    public bool AllowGoogleLogin { get; init; }
    public bool AllowPasswordLogin { get; init; }
    public bool RequireEmailConfirmation { get; init; }
    public string? AllowedEmailDomains { get; init; }
}
