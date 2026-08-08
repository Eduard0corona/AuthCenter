namespace AuthCenter.Infrastructure.Settings;

public sealed class ActionLinkSettings
{
    public string DefaultBaseUrl { get; init; } = string.Empty;
    public Dictionary<string, string> ApplicationBaseUrls { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public string PasswordResetPath { get; init; } = "/reset-password";
    public string EmailConfirmationPath { get; init; } = "/confirm-email";
    public string InvitationPath { get; init; } = "/accept-invitation";
    public string EmailChangePath { get; init; } = "/confirm-email-change";
    public string MagicLinkPath { get; init; } = "/magic-link";
}
