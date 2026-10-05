using System.Text.Json;
using System.Text.RegularExpressions;
using AuthCenter.Infrastructure.Services;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AuthCenter.UnitTests.Services;

/// <summary>
/// The emails as written: Spanish, the real lifetime of each link or code from configuration, a
/// "not you" line, a complete HTML document whose first link is the action, and the same content
/// as plain text. Sent directly (not through the outbox) they name no application.
/// </summary>
public class SmtpEmailServiceTests
{
    private const string Link = "https://login.example/magic-link?application=SHOP";

    [Fact]
    public async Task MagicLink_StatesTheConfiguredMinutes()
    {
        var mail = await SendAsync(service => service.SendMagicLinkAsync("ana@example.com", "Ana", "token", Link),
            new JwtSettings { MagicLinkTokenMinutes = 7 });

        Assert.Equal("Magic link", mail.Purpose);
        Assert.Equal("Tu enlace para entrar", mail.Subject);
        Assert.Contains("El enlace vence en 7 minutos y sólo funciona una vez.", mail.Text);
        Assert.Contains("El enlace vence en 7 minutos y sólo funciona una vez.", mail.Html);
        Assert.DoesNotContain("24 horas", mail.Text);
    }

    [Theory]
    [InlineData(1440, "24 horas")]
    [InlineData(60, "1 hora")]
    [InlineData(90, "90 minutos")]
    [InlineData(4320, "3 días")]
    public async Task IdentityTokenLinks_StateTheTokenProvidersLifespan(int minutes, string said)
    {
        var lifespan = TimeSpan.FromMinutes(minutes);

        var reset = await SendAsync(service => service.SendPasswordResetAsync("ana@example.com", "Ana", "token", Link), identityTokenLifespan: lifespan);
        var confirmation = await SendAsync(service => service.SendEmailConfirmationAsync("ana@example.com", "Ana", "token", Link), identityTokenLifespan: lifespan);
        var invitation = await SendAsync(service => service.SendInvitationAsync("ana@example.com", "Ana", "Tienda", "token", Link), identityTokenLifespan: lifespan);
        var change = await SendAsync(service => service.SendEmailChangeConfirmationAsync("ana@example.com", "Ana", "token", Link), identityTokenLifespan: lifespan);

        Assert.Contains($"El enlace vence en {said} y sólo funciona una vez.", reset.Text);
        Assert.Contains($"El enlace vence en {said}.", confirmation.Text);
        Assert.Contains($"El enlace vence en {said} y sólo funciona una vez.", invitation.Text);
        Assert.Contains($"El enlace vence en {said} y sólo funciona una vez.", change.Text);
    }

    [Fact]
    public async Task ActionEmails_AreCompleteSpanishDocuments_WhoseFirstLinkIsTheAction()
    {
        var mail = await SendAsync(service => service.SendEmailConfirmationAsync("ana@example.com", "Ana Muñoz", "a+b/c=", Link));

        Assert.Equal("Confirma tu correo", mail.Subject);
        Assert.StartsWith("<!doctype html>", mail.Html);
        Assert.Contains("<html lang=\"es\">", mail.Html);
        Assert.Contains("<meta charset=\"utf-8\">", mail.Html);
        Assert.Contains("<title>Confirma tu correo</title>", mail.Html);
        // Accents are written as they are; the end-to-end tests take the first link of the email.
        Assert.Contains("Hola, Ana Muñoz:", mail.Html);
        var links = Regex.Matches(mail.Html, "href=\"([^\"]+)\"").Select(match => match.Groups[1].Value.Replace("&amp;", "&")).ToList();
        Assert.Equal($"{Link}&token=a%2Bb%2Fc%3D&email=ana%40example.com", links[0]);
        Assert.All(links, link => Assert.Equal(links[0], link));
        Assert.Contains("Confirmar mi correo", mail.Html);
        Assert.Contains("Si no fuiste tú, ignora este mensaje; tu correo no quedará confirmado.", mail.Html);
        Assert.DoesNotContain("AuthCenter", mail.Html);
    }

    [Fact]
    public async Task EveryEmail_HasThePlainTextVersionOfTheSameContent()
    {
        var mail = await SendAsync(service => service.SendPasswordResetAsync("ana@example.com", "Ana", "token", Link));

        Assert.Equal(
            string.Join("\r\n",
                "Restablece tu contraseña",
                "",
                "Hola, Ana:",
                "",
                "Recibimos una solicitud para restablecer la contraseña de tu cuenta.",
                "",
                $"Restablecer mi contraseña: {Link}&token=token&email=ana%40example.com",
                "",
                "El enlace vence en 24 horas y sólo funciona una vez.",
                "",
                "Si no fuiste tú, ignora este mensaje; tu contraseña no cambiará.",
                "",
                "Este es un mensaje automático; no respondas a este correo."),
            mail.Text);
        Assert.DoesNotContain("<", mail.Text);
    }

    [Fact]
    public async Task MfaCode_IsAloneInItsElement_AndStatesTheLifetimeItWasSentWith()
    {
        var mail = await SendAsync(service => service.SendMfaEmailOtpAsync("ana@example.com", "Ana", "042913", validMinutes: 10));
        var unknown = await SendAsync(service => service.SendMfaEmailOtpAsync("ana@example.com", "Ana", "042913"));

        Assert.Equal("MFA Email OTP", mail.Purpose);
        Assert.Equal("Tu código de verificación", mail.Subject);
        Assert.Equal("042913", Regex.Match(mail.Html, @">\s*(\d{6})\s*<").Groups[1].Value);
        Assert.DoesNotContain("href=", mail.Html);
        Assert.Contains("El código vence en 10 minutos. No lo compartas con nadie.", mail.Text);
        Assert.Contains("Si no fuiste tú, cambia tu contraseña de inmediato", mail.Text);
        Assert.Contains("El código vence en unos minutos.", unknown.Text);
    }

    [Fact]
    public async Task SecurityNotice_IsTheCallersSpanishTitle_AndSaysWhatToDoIfItWasNotYou()
    {
        var mail = await SendAsync(service => service.SendSecurityNoticeAsync("ana@example.com", "<Ana>", "Se cambió tu contraseña", "Se cambió la contraseña de tu cuenta."));

        Assert.Equal("Security notice", mail.Purpose);
        Assert.Equal("Se cambió tu contraseña", mail.Subject);
        Assert.Contains("Si no reconoces esta actividad, cambia tu contraseña de inmediato.", mail.Html);
        Assert.Contains("Si no reconoces esta actividad, cambia tu contraseña de inmediato.", mail.Text);
        Assert.Contains("Hola, &lt;Ana&gt;:", mail.Html);
        Assert.DoesNotContain("AuthCenter", mail.Html);
    }

    private static async Task<PickedUpMail> SendAsync(Func<SmtpEmailService, Task> send, JwtSettings? jwt = null, TimeSpan? identityTokenLifespan = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"authcenter-mail-{Guid.NewGuid():N}");
        try
        {
            var service = new SmtpEmailService(
                Options.Create(new EmailSettings { DevelopmentPickupDirectory = directory, FromAddress = "no-reply@example.com" }),
                Options.Create(jwt ?? new JwtSettings()),
                Options.Create(new DataProtectionTokenProviderOptions { TokenLifespan = identityTokenLifespan ?? TimeSpan.FromDays(1) }),
                NullLogger<SmtpEmailService>.Instance);
            await send(service);
            var file = Assert.Single(Directory.GetFiles(directory, "*.json"));
            return JsonSerializer.Deserialize<PickedUpMail>(await File.ReadAllTextAsync(file), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The development pickup file, as the end-to-end tests read it.</summary>
    private sealed record PickedUpMail(string To, string Subject, string Purpose, string Html, string Text);
}
