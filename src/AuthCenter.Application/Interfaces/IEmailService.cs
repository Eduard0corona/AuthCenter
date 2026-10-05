namespace AuthCenter.Application.Interfaces;

/// <summary>
/// Sends the transactional emails. An emailed link speaks for the application its <c>application</c>
/// parameter names; the other emails name the application they are given, if any.
/// </summary>
public interface IEmailService
{
    Task SendPasswordResetAsync(string toEmail, string toName, string resetToken, string? callbackBaseUrl, CancellationToken ct = default);
    Task SendEmailConfirmationAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default);
    Task SendInvitationAsync(string toEmail, string toName, string applicationName, string token, string? callbackBaseUrl, CancellationToken ct = default);
    Task SendEmailChangeConfirmationAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default);
    Task SendMagicLinkAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default);

    /// <summary>
    /// Sends a one-time code, which works for <paramref name="validMinutes"/> (the lifetime depends on
    /// why it was sent), for a sign-in to <paramref name="applicationCode"/> when there is one.
    /// </summary>
    Task SendMfaEmailOtpAsync(string toEmail, string toName, string code, CancellationToken ct = default, int? validMinutes = null, string? applicationCode = null);

    /// <summary>
    /// Tells the user about a security-relevant change to their account (no links, no secrets), in the
    /// name of <paramref name="applicationCode"/> when the change happened while using it.
    /// </summary>
    Task SendSecurityNoticeAsync(string toEmail, string toName, string subject, string detail, CancellationToken ct = default, string? applicationCode = null);

    /// <summary>Tells the user something that waits for them (an access request to decide, a review) with a link to act on it.</summary>
    Task SendNotificationAsync(string toEmail, string toName, string subject, string detail, string actionUrl, string actionLabel, CancellationToken ct = default);
}
