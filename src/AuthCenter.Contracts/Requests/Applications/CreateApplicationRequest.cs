namespace AuthCenter.Contracts.Requests.Applications;

public class CreateApplicationRequest
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string RegistrationMode { get; init; } = "Open";
    public bool AllowGoogleLogin { get; init; }
    public bool AllowPasswordLogin { get; init; } = true;
    public bool RequireEmailConfirmation { get; init; }
    public string? AllowedEmailDomains { get; init; }
}
