using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using AuthCenter.Application.Interfaces;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using EmailPayload = AuthCenter.Infrastructure.Services.OutboxEmailService.EmailPayload;

namespace AuthCenter.Infrastructure.Services;

/// <summary>
/// Writes the emails, in Spanish and in the name of the application they are for, and sends each as
/// HTML with a plain-text alternative. AuthCenter itself is never named: without an application, an
/// email names no product at all.
/// </summary>
public class SmtpEmailService : IEmailService
{
    // Accents stay as they are (the document is UTF-8); markup characters are still encoded.
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);
    private const string AutomaticMessage = "Este es un mensaje automático; no respondas a este correo.";

    private readonly EmailSettings _settings;
    private readonly int _magicLinkMinutes;
    private readonly int _identityTokenMinutes;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(
        IOptions<EmailSettings> settings,
        IOptions<JwtSettings> jwtSettings,
        IOptions<DataProtectionTokenProviderOptions> identityTokenSettings,
        ILogger<SmtpEmailService> logger)
    {
        _settings = settings.Value;
        _magicLinkMinutes = jwtSettings.Value.MagicLinkTokenMinutes;
        // Password reset (invitations included), email confirmation and email change tokens all come
        // from Identity's default token provider, so they share its lifespan.
        _identityTokenMinutes = Math.Max(1, (int)identityTokenSettings.Value.TokenLifespan.TotalMinutes);
        _logger = logger;
    }

    // Sent directly instead of through the outbox, an email has no brand (sender and help link): finding it needs the database.
    public Task SendPasswordResetAsync(string toEmail, string toName, string resetToken, string? callbackBaseUrl, CancellationToken ct = default) =>
        SendAsync(new EmailPayload("password-reset", toEmail, toName, resetToken, callbackBaseUrl, null), ct);

    public Task SendEmailConfirmationAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default) =>
        SendAsync(new EmailPayload("email-confirmation", toEmail, toName, token, callbackBaseUrl, null), ct);

    public Task SendInvitationAsync(string toEmail, string toName, string applicationName, string token, string? callbackBaseUrl, CancellationToken ct = default) =>
        SendAsync(new EmailPayload("invitation", toEmail, toName, token, callbackBaseUrl, applicationName), ct);

    public Task SendEmailChangeConfirmationAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default) =>
        SendAsync(new EmailPayload("email-change", toEmail, toName, token, callbackBaseUrl, null), ct);

    public Task SendMagicLinkAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default) =>
        SendAsync(new EmailPayload("magic-link", toEmail, toName, token, callbackBaseUrl, null), ct);

    public Task SendMfaEmailOtpAsync(string toEmail, string toName, string code, CancellationToken ct = default, int? validMinutes = null, string? applicationCode = null) =>
        SendAsync(new EmailPayload("mfa-otp", toEmail, toName, code, null, null, ValidMinutes: validMinutes), ct);

    public Task SendSecurityNoticeAsync(string toEmail, string toName, string subject, string detail, CancellationToken ct = default, string? applicationCode = null) =>
        SendAsync(new EmailPayload("security-notice", toEmail, toName, detail, null, subject), ct);

    public Task SendNotificationAsync(string toEmail, string toName, string subject, string detail, string actionUrl, string actionLabel, CancellationToken ct = default) =>
        SendAsync(new EmailPayload("notification", toEmail, toName, detail, actionUrl, subject, actionLabel), ct);

    /// <summary>Writes the email a queued payload describes and sends it.</summary>
    internal async Task SendAsync(EmailPayload payload, CancellationToken ct)
    {
        var email = Render(payload);
        if (!string.IsNullOrWhiteSpace(_settings.DevelopmentPickupDirectory))
        {
            // Development and tests only (enforced at startup): the message becomes a JSON file.
            Directory.CreateDirectory(_settings.DevelopmentPickupDirectory);
            var file = Path.Combine(_settings.DevelopmentPickupDirectory, $"{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { to = payload.ToEmail, subject = email.Subject, purpose = email.Purpose, html = email.Html, text = email.Text }), ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.Host))
        {
            _logger.LogWarning(
                "Email SMTP not configured. {Purpose} email for {Email} was not sent.",
                email.Purpose, payload.ToEmail);
            throw new InvalidOperationException("Email SMTP is not configured.");
        }

        using var message = CreateMessage(payload.ToEmail, payload.ToName, email);
        using var client = new SmtpClient(_settings.Host, _settings.Port)
        {
            EnableSsl = _settings.EnableSsl,
            Credentials = new NetworkCredential(_settings.UserName, _settings.Password),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        try
        {
            await client.SendMailAsync(message, ct);
            _logger.LogInformation("{Purpose} email sent to {Email}", email.Purpose, payload.ToEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send {Purpose} email to {Email}", email.Purpose, payload.ToEmail);
            throw;
        }
    }

    /// <summary>The message SMTP sends: the plain-text and the HTML version of the email, as alternatives.</summary>
    internal MailMessage CreateMessage(string toEmail, string toName, RenderedEmail email)
    {
        var message = new MailMessage
        {
            From = new MailAddress(_settings.FromAddress, email.FromName, Encoding.UTF8),
            Subject = email.Subject,
            SubjectEncoding = Encoding.UTF8,
            HeadersEncoding = Encoding.UTF8
        };
        message.To.Add(new MailAddress(toEmail, toName, Encoding.UTF8));
        // Plain text first: a client shows the last alternative it supports, so HTML wins where it can.
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(email.Text, Encoding.UTF8, MediaTypeNames.Text.Plain));
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(email.Html, Encoding.UTF8, MediaTypeNames.Text.Html));
        return message;
    }

    /// <summary>
    /// The email a payload describes. The purpose (for logs and the development pickup) and the kinds
    /// are internal names other code selects emails by, so they stay as they are.
    /// </summary>
    internal RenderedEmail Render(EmailPayload payload)
    {
        var app = payload.ApplicationDisplayName;
        // A payload queued before the brand was resolved still knows the invitation's application.
        var invitedTo = app ?? (string.IsNullOrWhiteSpace(payload.ApplicationName) ? null : payload.ApplicationName);
        var content = payload.Kind switch
        {
            "password-reset" => new EmailContent(
                "Password reset",
                app is null ? "Restablece tu contraseña" : $"Restablece tu contraseña de {app}",
                "Restablece tu contraseña",
                app is null
                    ? "Recibimos una solicitud para restablecer la contraseña de tu cuenta."
                    : $"Recibimos una solicitud para restablecer la contraseña de tu cuenta en {app}.",
                "Si no fuiste tú, ignora este mensaje; tu contraseña no cambiará.",
                Validity: $"El enlace vence en {Duration(payload.ValidMinutes ?? _identityTokenMinutes)} y sólo funciona una vez.",
                ActionLabel: "Restablecer mi contraseña",
                ActionUrl: BuildTokenLink(payload.ActionUrl, payload.ToEmail, payload.Secret)),
            "email-confirmation" => new EmailContent(
                "Email confirmation",
                app is null ? "Confirma tu correo" : $"Confirma tu correo para {app}",
                "Confirma tu correo",
                app is null
                    ? "Confirma que esta dirección es tuya para terminar de crear tu cuenta."
                    : $"Confirma que esta dirección es tuya para terminar de crear tu cuenta en {app}.",
                "Si no fuiste tú, ignora este mensaje; tu correo no quedará confirmado.",
                Validity: $"El enlace vence en {Duration(payload.ValidMinutes ?? _identityTokenMinutes)}.",
                ActionLabel: "Confirmar mi correo",
                ActionUrl: BuildTokenLink(payload.ActionUrl, payload.ToEmail, payload.Secret)),
            "invitation" => new EmailContent(
                "Invitation",
                invitedTo is null ? "Te invitaron a crear tu cuenta" : $"Te invitaron a {invitedTo}",
                invitedTo is null ? "Te invitaron a crear tu cuenta" : $"Te invitaron a {invitedTo}",
                (invitedTo is null ? "Te dieron acceso con este correo." : $"Te dieron acceso a {invitedTo} con este correo.") +
                " Acepta la invitación y elige tu contraseña para empezar.",
                "Si no esperabas esta invitación, puedes ignorar este mensaje.",
                Validity: $"El enlace vence en {Duration(payload.ValidMinutes ?? _identityTokenMinutes)} y sólo funciona una vez.",
                ActionLabel: "Aceptar la invitación",
                ActionUrl: BuildTokenLink(payload.ActionUrl, payload.ToEmail, payload.Secret)),
            "email-change" => new EmailContent(
                "Email change confirmation",
                "Confirma tu nuevo correo",
                "Confirma tu nuevo correo",
                (app is null
                    ? "Confirma que esta dirección es tuya para empezar a usarla en tu cuenta."
                    : $"Confirma que esta dirección es tuya para empezar a usarla en tu cuenta de {app}.") +
                " Hasta entonces, tu cuenta sigue usando el correo anterior.",
                "Si no fuiste tú, ignora este mensaje; esta dirección no se agregará a ninguna cuenta.",
                Validity: $"El enlace vence en {Duration(payload.ValidMinutes ?? _identityTokenMinutes)} y sólo funciona una vez.",
                ActionLabel: "Confirmar mi nuevo correo",
                ActionUrl: BuildTokenLink(payload.ActionUrl, payload.ToEmail, payload.Secret)),
            "magic-link" => new EmailContent(
                "Magic link",
                app is null ? "Tu enlace para entrar" : $"Tu enlace para entrar a {app}",
                app is null ? "Tu enlace para entrar" : $"Tu enlace para entrar a {app}",
                // The sign-in continues in the browser that asked for the link (see the hosted login).
                (app is null ? "Usa este enlace para entrar sin contraseña." : $"Usa este enlace para entrar a {app} sin contraseña.") +
                " Ábrelo en el mismo navegador donde lo pediste.",
                "Si no fuiste tú, ignora este mensaje; nadie podrá entrar a tu cuenta sin este enlace.",
                Validity: $"El enlace vence en {Duration(payload.ValidMinutes ?? _magicLinkMinutes)} y sólo funciona una vez.",
                ActionLabel: "Entrar",
                ActionUrl: BuildTokenLink(payload.ActionUrl, payload.ToEmail, payload.Secret)),
            "mfa-otp" => new EmailContent(
                "MFA Email OTP",
                "Tu código de verificación",
                "Tu código de verificación",
                app is null ? "Escribe este código para continuar:" : $"Escribe este código para continuar en {app}:",
                "Si no fuiste tú, cambia tu contraseña de inmediato: alguien podría estar intentando entrar a tu cuenta.",
                // The lifetime depends on why the code was sent; a payload queued before it said so has none.
                Validity: $"El código vence en {(payload.ValidMinutes is { } minutes ? Duration(minutes) : "unos minutos")}. No lo compartas con nadie.",
                Code: payload.Secret),
            "security-notice" => new EmailContent(
                "Security notice",
                payload.ApplicationName ?? "Aviso de seguridad",
                payload.ApplicationName ?? "Aviso de seguridad",
                payload.Secret,
                "Si no reconoces esta actividad, cambia tu contraseña de inmediato."),
            "notification" => new EmailContent(
                "Access governance",
                payload.ApplicationName ?? "Tienes un aviso",
                payload.ApplicationName ?? "Tienes un aviso",
                payload.Secret,
                "Si no esperabas este mensaje, avísale a tu administrador.",
                ActionLabel: payload.ActionLabel ?? "Abrir",
                ActionUrl: string.IsNullOrWhiteSpace(payload.ActionUrl) ? null : payload.ActionUrl),
            _ => throw new InvalidOperationException($"Unsupported outbox email kind '{payload.Kind}'.")
        };

        var subject = OneLine(content.Subject);
        return new RenderedEmail(
            content.Purpose,
            app ?? _settings.FromName,
            subject,
            RenderHtml(content, subject, payload.ToName, app, payload.ApplicationSupportUrl),
            RenderText(content, payload.ToName, app, payload.ApplicationSupportUrl));
    }

    /// <summary>
    /// A complete, readable document (text contrast of at least 4.5:1) with one action. The action is
    /// the first link, its address is repeated in case the button does not work, and a code is the
    /// only content of its element.
    /// </summary>
    private static string RenderHtml(EmailContent content, string subject, string toName, string? app, string? supportUrl)
    {
        var html = new StringBuilder();
        html.Append($"""
            <!doctype html>
            <html lang="es">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{Encoder.Encode(subject)}</title>
            </head>
            <body style="margin:0;padding:0;background-color:#f3f4f6">
            <div style="max-width:560px;margin:0 auto;padding:24px 16px;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:1.5;color:#1f2937">
            <div style="background-color:#ffffff;border-radius:8px;padding:32px 24px">
            <h1 style="margin:0 0 16px;font-size:22px;line-height:1.3;color:#111827">{Encoder.Encode(content.Heading)}</h1>
            <p style="margin:0 0 16px">{Encoder.Encode(Greeting(toName))}</p>
            <p style="margin:0 0 16px">{Encoder.Encode(content.Intro)}</p>

            """);
        if (content.ActionUrl is not null)
            html.Append($"""
                <p style="margin:24px 0"><a href="{Encoder.Encode(content.ActionUrl)}" style="display:inline-block;padding:12px 24px;border-radius:6px;background-color:#1d4ed8;color:#ffffff;font-weight:bold;text-decoration:none">{Encoder.Encode(content.ActionLabel ?? "Abrir")}</a></p>

                """);
        if (content.Code is not null)
            html.Append($"""
                <p style="margin:24px 0;font-family:'Courier New',Courier,monospace;font-size:32px;font-weight:bold;letter-spacing:6px;color:#111827">{Encoder.Encode(content.Code)}</p>

                """);
        if (content.Validity is not null)
            html.Append($"""
                <p style="margin:0 0 16px">{Encoder.Encode(content.Validity)}</p>

                """);
        if (content.ActionUrl is not null)
            html.Append($"""
                <p style="margin:0 0 16px;font-size:14px;color:#4b5563">Si el botón no funciona, copia y pega este enlace en tu navegador:<br><a href="{Encoder.Encode(content.ActionUrl)}" style="color:#1d4ed8;word-break:break-all">{Encoder.Encode(content.ActionUrl)}</a></p>

                """);
        html.Append($"""
            <p style="margin:0;font-size:14px;color:#4b5563">{Encoder.Encode(content.NotYou)}</p>
            </div>
            <p style="margin:16px 0 0;font-size:13px;color:#4b5563">{FooterHtml(app, supportUrl)}{Encoder.Encode(AutomaticMessage)}</p>
            </div>
            </body>
            </html>
            """);
        return html.ToString();
    }

    // The application and its help link, never AuthCenter: with neither, only the automatic-message line.
    private static string FooterHtml(string? app, string? supportUrl)
    {
        var parts = new List<string>();
        if (app is not null)
            parts.Add(Encoder.Encode(app));
        if (supportUrl is not null)
            parts.Add($"""<a href="{Encoder.Encode(supportUrl)}" style="color:#1d4ed8">Ayuda</a>""");
        return parts.Count == 0 ? string.Empty : $"{string.Join(" · ", parts)}<br>";
    }

    /// <summary>The same content as plain text, for clients that do not show HTML.</summary>
    private static string RenderText(EmailContent content, string toName, string? app, string? supportUrl)
    {
        var lines = new List<string> { content.Heading, string.Empty, Greeting(toName), string.Empty, content.Intro, string.Empty };
        if (content.ActionUrl is not null)
            lines.AddRange([$"{content.ActionLabel ?? "Abrir"}: {content.ActionUrl}", string.Empty]);
        if (content.Code is not null)
            lines.AddRange([content.Code, string.Empty]);
        if (content.Validity is not null)
            lines.AddRange([content.Validity, string.Empty]);
        lines.AddRange([content.NotYou, string.Empty]);
        var help = supportUrl is null ? null : $"Ayuda: {supportUrl}";
        if (app is not null || help is not null)
            lines.Add(string.Join(" · ", new[] { app, help }.Where(item => item is not null)));
        lines.Add(AutomaticMessage);
        return string.Join("\r\n", lines);
    }

    private static string Greeting(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Hola:" : $"Hola, {name.Trim()}:";

    /// <summary>A lifetime as people say it: "15 minutos", "1 hora", "24 horas", "3 días".</summary>
    private static string Duration(int minutes) => minutes switch
    {
        <= 1 => "1 minuto",
        < 60 => $"{minutes} minutos",
        60 => "1 hora",
        _ when minutes % 1440 == 0 && minutes > 1440 => $"{minutes / 1440} días",
        _ when minutes % 60 == 0 => $"{minutes / 60} horas",
        _ => $"{minutes} minutos"
    };

    // A subject is a single header line.
    private static string OneLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string BuildTokenLink(string? callbackBaseUrl, string email, string token)
    {
        if (string.IsNullOrWhiteSpace(callbackBaseUrl))
            return $"token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}";

        var sep = callbackBaseUrl.Contains('?') ? "&" : "?";
        return $"{callbackBaseUrl}{sep}token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}";
    }

    /// <summary>What an email says, before it becomes HTML and plain text.</summary>
    private sealed record EmailContent(
        string Purpose,
        string Subject,
        string Heading,
        string Intro,
        string NotYou,
        string? Validity = null,
        string? ActionLabel = null,
        string? ActionUrl = null,
        string? Code = null);

    /// <summary>An email ready to send, as HTML and as plain text with the same content.</summary>
    internal sealed record RenderedEmail(string Purpose, string FromName, string Subject, string Html, string Text);
}
