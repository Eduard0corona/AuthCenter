namespace AuthCenter.Application.Interfaces;

public interface IEmailService
{
    Task SendPasswordResetAsync(string toEmail, string toName, string resetToken, string? callbackBaseUrl, CancellationToken ct = default);
    Task SendEmailConfirmationAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default);
    Task SendInvitationAsync(string toEmail, string toName, string applicationName, string token, string? callbackBaseUrl, CancellationToken ct = default);
    Task SendEmailChangeConfirmationAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default);
    Task SendMagicLinkAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default);
    Task SendMfaEmailOtpAsync(string toEmail, string toName, string code, CancellationToken ct = default);

    /// <summary>Tells the user about a security-relevant change to their account (no links, no secrets).</summary>
    Task SendSecurityNoticeAsync(string toEmail, string toName, string subject, string detail, CancellationToken ct = default);

    /// <summary>Tells the user something that waits for them (an access request to decide, a review) with a link to act on it.</summary>
    Task SendNotificationAsync(string toEmail, string toName, string subject, string detail, string actionUrl, string actionLabel, CancellationToken ct = default);
}
