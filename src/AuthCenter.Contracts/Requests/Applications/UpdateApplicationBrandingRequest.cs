namespace AuthCenter.Contracts.Requests.Applications;

public sealed class UpdateApplicationBrandingRequest
{
    public string DisplayName { get; init; } = string.Empty;
    public string PrimaryColor { get; init; } = "#2563EB";
    public string BackgroundColor { get; init; } = "#F8FAFC";
    public string? LogoUrl { get; init; }
    public string? SupportUrl { get; init; }
    public string? PrivacyUrl { get; init; }
    public string? TermsUrl { get; init; }
}
