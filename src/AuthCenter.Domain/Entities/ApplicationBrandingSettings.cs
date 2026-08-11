namespace AuthCenter.Domain.Entities;

public sealed class ApplicationBrandingSettings
{
    public Guid Id { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string PrimaryColor { get; set; } = "#2563EB";
    public string BackgroundColor { get; set; } = "#F8FAFC";
    public string? LogoUrl { get; set; }
    public string? SupportUrl { get; set; }
    public string? PrivacyUrl { get; set; }
    public string? TermsUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ApplicationSystem ApplicationSystem { get; set; } = null!;
}
