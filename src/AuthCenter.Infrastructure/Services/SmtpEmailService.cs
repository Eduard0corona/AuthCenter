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
        var resetLink = BuildResetLink(callbackBaseUrl, toEmail, resetToken);
        var subject = "Restablecer contraseña — AuthCenter";
        var body = BuildResetEmailBody(toName, resetLink, resetToken);

        if (string.IsNullOrWhiteSpace(_settings.Host))
        {
            // Sin SMTP configurado: loguear el token para desarrollo
            _logger.LogWarning(
                "Email SMTP not configured. Password reset token for {Email}: {Token} | Link: {Link}",
                toEmail, resetToken, resetLink);
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
            _logger.LogInformation("Password reset email sent to {Email}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send password reset email to {Email}", toEmail);
            throw;
        }
    }

    private static string BuildResetLink(string? callbackBaseUrl, string email, string token)
    {
        if (string.IsNullOrWhiteSpace(callbackBaseUrl))
            return $"token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}";

        var sep = callbackBaseUrl.Contains('?') ? "&" : "?";
        return $"{callbackBaseUrl}{sep}token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}";
    }

    private static string BuildResetEmailBody(string name, string resetLink, string rawToken) => $"""
        <html><body style="font-family:sans-serif;max-width:600px;margin:auto">
          <h2>Restablecer contraseña</h2>
          <p>Hola <strong>{name}</strong>,</p>
          <p>Recibimos una solicitud para restablecer tu contraseña. Haz clic en el siguiente enlace
          (válido por 24 horas):</p>
          <p><a href="{resetLink}" style="background:#0066cc;color:white;padding:12px 20px;border-radius:4px;text-decoration:none">
            Restablecer contraseña
          </a></p>
          <p>O usa este token directamente: <code>{rawToken}</code></p>
          <p>Si no solicitaste este cambio, ignora este correo.</p>
          <hr/><p style="color:#888;font-size:12px">AuthCenter — Servicio de identidad centralizado</p>
        </body></html>
        """;
}
