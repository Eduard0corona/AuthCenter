using System.Net;
using System.Net.Mail;
using AuthCenter.Application.Interfaces;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services;

public class SmtpEmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IOptions<EmailSettings> settings, ILogger<SmtpEmailService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendPasswordResetAsync(string toEmail, string toName, string resetToken, string? callbackBaseUrl, CancellationToken ct = default)
    {
        var resetLink = BuildTokenLink(callbackBaseUrl, toEmail, resetToken);
        var body = BuildActionEmailBody(toName, "Reset password", "Reset password", resetLink, resetToken);

        await SendAsync(toEmail, toName, "Reset password - AuthCenter", body, "Password reset", resetToken, resetLink, ct);
    }

    public async Task SendEmailConfirmationAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default)
    {
        var link = BuildTokenLink(callbackBaseUrl, toEmail, token);
        var body = BuildActionEmailBody(toName, "Confirm your email", "Confirm email", link, token);

        await SendAsync(toEmail, toName, "Confirm email - AuthCenter", body, "Email confirmation", token, link, ct);
    }

    public async Task SendInvitationAsync(string toEmail, string toName, string applicationName, string token, string? callbackBaseUrl, CancellationToken ct = default)
    {
        var link = BuildTokenLink(callbackBaseUrl, toEmail, token);
        var body = BuildActionEmailBody(toName, $"You were invited to {applicationName}", "Accept invitation", link, token);

        await SendAsync(toEmail, toName, $"Invitation to {applicationName} - AuthCenter", body, "Invitation", token, link, ct);
    }

    private async Task SendAsync(
        string toEmail,
        string toName,
        string subject,
        string body,
        string purpose,
        string token,
        string link,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.Host))
        {
            _logger.LogWarning(
                "Email SMTP not configured. {Purpose} token for {Email}: {Token} | Link: {Link}",
                purpose, toEmail, token, link);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_settings.FromAddress, _settings.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };
        message.To.Add(new MailAddress(toEmail, toName));

        using var client = new SmtpClient(_settings.Host, _settings.Port)
        {
            EnableSsl = _settings.EnableSsl,
            Credentials = new NetworkCredential(_settings.UserName, _settings.Password),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        try
        {
            await client.SendMailAsync(message, ct);
            _logger.LogInformation("{Purpose} email sent to {Email}", purpose, toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send {Purpose} email to {Email}", purpose, toEmail);
            throw;
        }
    }

    private static string BuildTokenLink(string? callbackBaseUrl, string email, string token)
    {
        if (string.IsNullOrWhiteSpace(callbackBaseUrl))
            return $"token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}";

        var sep = callbackBaseUrl.Contains('?') ? "&" : "?";
        return $"{callbackBaseUrl}{sep}token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}";
    }

    private static string BuildActionEmailBody(string name, string title, string button, string link, string rawToken) => $"""
        <html><body style="font-family:sans-serif;max-width:600px;margin:auto">
          <h2>{title}</h2>
          <p>Hello <strong>{name}</strong>,</p>
          <p>Use the following link to continue:</p>
          <p><a href="{link}" style="background:#0066cc;color:white;padding:12px 20px;border-radius:4px;text-decoration:none">
            {button}
          </a></p>
          <p>Or use this token directly: <code>{rawToken}</code></p>
          <hr/><p style="color:#888;font-size:12px">AuthCenter - centralized identity service</p>
        </body></html>
        """;
}
