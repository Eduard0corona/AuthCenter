using System.Net;
using System.Net.Mail;
using System.Text.Encodings.Web;
using System.Text.Json;
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
        var link = BuildTokenLink(callbackBaseUrl, toEmail, resetToken);
        var body = BuildActionEmailBody(toName, "Reset password", "Reset password", link);
        await SendAsync(toEmail, toName, "Reset password - AuthCenter", body, "Password reset", ct);
    }

    public async Task SendEmailConfirmationAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default)
    {
        var link = BuildTokenLink(callbackBaseUrl, toEmail, token);
        var body = BuildActionEmailBody(toName, "Confirm your email", "Confirm email", link);
        await SendAsync(toEmail, toName, "Confirm email - AuthCenter", body, "Email confirmation", ct);
    }

    public async Task SendInvitationAsync(string toEmail, string toName, string applicationName, string token, string? callbackBaseUrl, CancellationToken ct = default)
    {
        var link = BuildTokenLink(callbackBaseUrl, toEmail, token);
        var body = BuildActionEmailBody(toName, $"You were invited to {applicationName}", "Accept invitation", link);
        await SendAsync(toEmail, toName, $"Invitation to {applicationName} - AuthCenter", body, "Invitation", ct);
    }

    public async Task SendEmailChangeConfirmationAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default)
    {
        var link = BuildTokenLink(callbackBaseUrl, toEmail, token);
        var body = BuildActionEmailBody(toName, "Confirm your new email address", "Confirm email change", link);
        await SendAsync(toEmail, toName, "Confirm email change - AuthCenter", body, "Email change confirmation", ct);
    }

    public async Task SendMagicLinkAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default)
    {
        var link = BuildTokenLink(callbackBaseUrl, toEmail, token);
        var body = BuildActionEmailBody(toName, "Sign in to your account", "Sign in", link);
        await SendAsync(toEmail, toName, "Sign in link - AuthCenter", body, "Magic link", ct);
    }

    public async Task SendMfaEmailOtpAsync(string toEmail, string toName, string code, CancellationToken ct = default)
    {
        var safeName = HtmlEncoder.Default.Encode(toName);
        var safeCode = HtmlEncoder.Default.Encode(code);
        var body = $"""
        <html><body style="font-family:sans-serif;max-width:600px;margin:auto">
          <h2>Your sign-in code</h2>
          <p>Hello <strong>{safeName}</strong>,</p>
          <p>Your one-time sign-in code (valid for a few minutes):</p>
          <p style="font-size:36px;letter-spacing:10px;font-weight:bold;font-family:monospace;color:#0066cc">{safeCode}</p>
          <p>If you didn't request this code, ignore this email.</p>
          <hr/><p style="color:#888;font-size:12px">AuthCenter - centralized identity service</p>
        </body></html>
        """;

        await SendAsync(toEmail, toName, "Your sign-in code - AuthCenter", body, "MFA Email OTP", ct);
    }

    public async Task SendSecurityNoticeAsync(string toEmail, string toName, string subject, string detail, CancellationToken ct = default)
    {
        var encoder = HtmlEncoder.Default;
        var body = $"""
        <html><body style="font-family:sans-serif;max-width:600px;margin:auto">
          <h2>{encoder.Encode(subject)}</h2>
          <p>Hello <strong>{encoder.Encode(toName)}</strong>,</p>
          <p>{encoder.Encode(detail)}</p>
          <p>If this was not you, sign in to your account portal, review your sessions and factors, and contact support.</p>
          <hr/><p style="color:#888;font-size:12px">AuthCenter - centralized identity service</p>
        </body></html>
        """;
        await SendAsync(toEmail, toName, $"{subject} - AuthCenter", body, "Security notice", ct);
    }

    private async Task SendAsync(
        string toEmail,
        string toName,
        string subject,
        string body,
        string purpose,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_settings.DevelopmentPickupDirectory))
        {
            // Development and tests only (enforced at startup): the message becomes a JSON file.
            Directory.CreateDirectory(_settings.DevelopmentPickupDirectory);
            var file = Path.Combine(_settings.DevelopmentPickupDirectory, $"{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { to = toEmail, subject, purpose, html = body }), ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.Host))
        {
            _logger.LogWarning(
                "Email SMTP not configured. {Purpose} email for {Email} was not sent.",
                purpose, toEmail);
            throw new InvalidOperationException("Email SMTP is not configured.");
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

    private static string BuildActionEmailBody(string name, string title, string button, string link)
    {
        var encoder = HtmlEncoder.Default;
        var safeName = encoder.Encode(name);
        var safeTitle = encoder.Encode(title);
        var safeButton = encoder.Encode(button);
        var safeLink = encoder.Encode(link);

        return $"""
        <html><body style="font-family:sans-serif;max-width:600px;margin:auto">
          <h2>{safeTitle}</h2>
          <p>Hello <strong>{safeName}</strong>,</p>
          <p>Use the following link to continue (valid for 24 hours):</p>
          <p><a href="{safeLink}" style="background:#0066cc;color:white;padding:12px 20px;border-radius:4px;text-decoration:none">
            {safeButton}
          </a></p>
          <p>If the button doesn't work, copy and paste this URL into your browser:</p>
          <p style="word-break:break-all;font-size:13px;color:#555">{safeLink}</p>
          <hr/><p style="color:#888;font-size:12px">AuthCenter - centralized identity service</p>
        </body></html>
        """;
    }
}
