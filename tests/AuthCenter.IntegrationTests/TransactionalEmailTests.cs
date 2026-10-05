using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Applications;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Services;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// The emails people receive: in Spanish, in the name of the application they are for (never in
/// AuthCenter's), with the real lifetime of their link or code, a "not you" line and a plain-text
/// alternative. The application is resolved when the email is queued.
/// </summary>
public sealed class TransactionalEmailTests : IClassFixture<HttpsAuthCenterFactory>
{
    private const string SupportUrl = "https://ayuda.paquetenvia.example/";
    private readonly HttpsAuthCenterFactory _factory;

    public TransactionalEmailTests(HttpsAuthCenterFactory factory) => _factory = factory;

    [Fact]
    public async Task Confirmation_OfABrandedApplication_IsSpanish_AndSpeaksOnlyForTheApplication()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateBrandedApplicationAsync(admin, "Paquetenvia");
        var email = $"brand-{Guid.NewGuid():N}@example.com";
        using var client = _factory.CreateAuthCenterClient();

        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            FullName = "Ana Muñoz", Email = email, Password = TestSecretGenerator.CreatePassword(), ApplicationCode = application.Code
        });

        var payload = await LatestAsync(email, "email-confirmation");
        // The brand's display name, not the application's internal name.
        Assert.Equal("Paquetenvia", payload.ApplicationDisplayName);
        Assert.Equal(SupportUrl, payload.ApplicationSupportUrl);
        var mail = Render(payload);
        Assert.Equal("Email confirmation", mail.Purpose);
        Assert.Equal("Confirma tu correo para Paquetenvia", mail.Subject);
        Assert.Equal("Paquetenvia", mail.FromName);
        Assert.StartsWith("<!doctype html>", mail.Html);
        Assert.Contains("<html lang=\"es\">", mail.Html);
        Assert.Contains("Confirma que esta dirección es tuya para terminar de crear tu cuenta en Paquetenvia.", mail.Text);
        Assert.Contains("Si no fuiste tú, ignora este mensaje; tu correo no quedará confirmado.", mail.Text);
        Assert.Contains($"Paquetenvia · Ayuda: {SupportUrl}", mail.Text);
        // The action is the first link and the help link comes last.
        var links = Regex.Matches(mail.Html, "href=\"([^\"]+)\"").Select(match => match.Groups[1].Value.Replace("&amp;", "&")).ToList();
        Assert.StartsWith($"{payload.ActionUrl}&token=", links[0]);
        Assert.Contains($"Confirmar mi correo: {links[0]}", mail.Text);
        Assert.Equal(SupportUrl, links[^1]);
        foreach (var part in new[] { mail.Subject, mail.FromName, mail.Html, mail.Text })
            Assert.DoesNotContain("authcenter", Words(part), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EveryEmail_IsSentAsPlainTextAndHtmlAlternatives_FromTheApplication()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateBrandedApplicationAsync(admin, "Paquetenvía Exprés");
        var email = await CreateUserAsync(application.Code);
        await _factory.CreateAuthCenterClient().PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest { Email = email, ApplicationCode = application.Code });
        var payload = await LatestAsync(email, "password-reset");
        await using var scope = _factory.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<SmtpEmailService>();
        var mail = sender.Render(payload);

        using var message = sender.CreateMessage(email, "Ana Muñoz", mail);

        Assert.Equal("Paquetenvía Exprés", message.From!.DisplayName);
        Assert.Equal(scope.ServiceProvider.GetRequiredService<IOptions<EmailSettings>>().Value.FromAddress, message.From.Address);
        Assert.Equal("Restablece tu contraseña de Paquetenvía Exprés", message.Subject);
        Assert.Collection(message.AlternateViews,
            text => Assert.Equal(("text/plain", "utf-8", mail.Text), (text.ContentType.MediaType, text.ContentType.CharSet, Read(text))),
            html => Assert.Equal(("text/html", "utf-8", mail.Html), (html.ContentType.MediaType, html.ContentType.CharSet, Read(html))));
        // What SMTP would send: one multipart/alternative message with both versions.
        var pickup = Directory.CreateTempSubdirectory("authcenter-eml-").FullName;
        try
        {
            using (var smtp = new SmtpClient { DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory, PickupDirectoryLocation = pickup })
                smtp.Send(message);
            var eml = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(pickup)));
            Assert.Contains("Content-Type: multipart/alternative", eml);
            Assert.Contains("Content-Type: text/plain; charset=utf-8", eml);
            Assert.Contains("Content-Type: text/html; charset=utf-8", eml);
            Assert.True(eml.IndexOf("text/plain", StringComparison.Ordinal) < eml.IndexOf("text/html", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(pickup, recursive: true);
        }
    }

    [Fact]
    public async Task EmailsOfTheSystemApplication_NameNoProduct()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            // As the migrations leave it: a branding row under AuthCenter's own name (and here a help link).
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            var system = await db.ApplicationSystems.Include(item => item.BrandingSettings).SingleAsync(item => item.Code == DomainConstants.SystemCodes.AuthCenter);
            if (system.BrandingSettings is null)
                db.ApplicationBrandingSettings.Add(system.BrandingSettings = new ApplicationBrandingSettings { Id = Guid.NewGuid(), ApplicationSystemId = system.Id, CreatedAt = DateTime.UtcNow });
            system.BrandingSettings.DisplayName = "AuthCenter";
            system.BrandingSettings.SupportUrl = SupportUrl;
            await db.SaveChangesAsync();
        }
        var email = await CreateUserAsync(DomainConstants.SystemCodes.AuthCenter);
        await _factory.CreateAuthCenterClient().PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest
        {
            Email = email, ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var emails = scope.ServiceProvider.GetRequiredService<IEmailService>();
            await emails.SendInvitationAsync(email, "Ana", "AuthCenter", "token",
                $"https://login.example/accept-invitation?application={DomainConstants.SystemCodes.AuthCenter}");
            await emails.SendSecurityNoticeAsync(email, "Ana", "Se activó la verificación en dos pasos", "Se configuró una app de autenticación para tu cuenta al iniciar sesión.",
                applicationCode: DomainConstants.SystemCodes.AuthCenter);
        }

        var reset = await LatestAsync(email, "password-reset");
        Assert.Contains($"application={DomainConstants.SystemCodes.AuthCenter}", reset.ActionUrl);
        var invitation = await LatestAsync(email, "invitation");
        var notice = await LatestAsync(email, "security-notice");
        Assert.All(new[] { reset, invitation, notice }, payload => Assert.Equal((null, null), (payload.ApplicationDisplayName, payload.ApplicationSupportUrl)));
        Assert.Null(invitation.ApplicationName);
        Assert.Equal("Restablece tu contraseña", Render(reset).Subject);
        Assert.Equal("Te invitaron a crear tu cuenta", Render(invitation).Subject);
        var fromName = _factory.Services.GetRequiredService<IOptions<EmailSettings>>().Value.FromName;
        foreach (var mail in new[] { Render(reset), Render(invitation), Render(notice) })
        {
            // Without an application the sender is the configured one, and the email names no product.
            Assert.Equal(fromName, mail.FromName);
            foreach (var part in new[] { mail.Subject, mail.Html, mail.Text })
                Assert.DoesNotContain("authcenter", Words(part), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SystemApplication_IsNamedOnlyOnceItIsCalledSomethingElse()
    {
        await using var db = new AuthCenterDbContext(new DbContextOptionsBuilder<AuthCenterDbContext>()
            .UseInMemoryDatabase($"email-branding-{Guid.NewGuid():N}").Options);
        var system = new ApplicationSystem { Id = Guid.NewGuid(), Code = DomainConstants.SystemCodes.AuthCenter, Name = "AuthCenter", IsActive = true, CreatedAt = DateTime.UtcNow };
        var shop = new ApplicationSystem { Id = Guid.NewGuid(), Code = "SHOP", Name = "Tienda interna", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.ApplicationSystems.AddRange(system, shop);
        await db.SaveChangesAsync();

        // No branding row, then the one the migrations create: either way, no application.
        Assert.Equal(new EmailBrand(null, null), await EmailBranding.ForCodeAsync(db, system.Code, CancellationToken.None));
        var branding = new ApplicationBrandingSettings
        {
            Id = Guid.NewGuid(), ApplicationSystemId = system.Id, DisplayName = "AuthCenter", SupportUrl = "https://ayuda.contoso.example/", CreatedAt = DateTime.UtcNow
        };
        db.ApplicationBrandingSettings.Add(branding);
        await db.SaveChangesAsync();
        Assert.Equal(new EmailBrand(null, null), await EmailBranding.ForCodeAsync(db, system.Code, CancellationToken.None));
        Assert.Equal("la consola de administración", await EmailBranding.NameAsync(db, system.Id, CancellationToken.None));
        Assert.Equal(new EmailBrand("Tienda interna", null), await EmailBranding.ForCodeAsync(db, shop.Code, CancellationToken.None));
        Assert.Null(await EmailBranding.ForCodeAsync(db, "NOPE", CancellationToken.None));

        branding.DisplayName = "authcenter";
        await db.SaveChangesAsync();
        Assert.Equal(new EmailBrand(null, null), await EmailBranding.ForCodeAsync(db, system.Code, CancellationToken.None));

        branding.DisplayName = "Cuentas Contoso";
        await db.SaveChangesAsync();
        Assert.Equal(new EmailBrand("Cuentas Contoso", "https://ayuda.contoso.example/"), await EmailBranding.ForCodeAsync(db, system.Code, CancellationToken.None));
        Assert.Equal("Cuentas Contoso", await EmailBranding.NameAsync(db, system.Id, CancellationToken.None));
    }

    [Fact]
    public async Task MagicLink_StatesTheConfiguredMinutes_WhichIsHowLongItsTokenLasts()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:MagicLinkTokenMinutes"] = "7" })));
        using var admin = await CreateAdminClientAsync();
        var application = await CreateBrandedApplicationAsync(admin, "Paquetenvia", magicLink: true);
        var email = await CreateUserAsync(application.Code);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(HttpsAuthCenterFactory.Authority) });
        var requestedAt = DateTime.UtcNow;

        var requested = await client.PostAsJsonAsync("/api/auth/magic-link/request", new MagicLinkRequest { Email = email, ApplicationCode = application.Code });

        Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
        var payload = await LatestAsync(email, "magic-link");
        var expires = new JwtSecurityTokenHandler().ReadJwtToken(payload.Secret).ValidTo;
        Assert.InRange(expires, requestedAt.AddMinutes(7).AddSeconds(-2), DateTime.UtcNow.AddMinutes(7).AddSeconds(2));
        await using var scope = factory.Services.CreateAsyncScope();
        var mail = scope.ServiceProvider.GetRequiredService<SmtpEmailService>().Render(payload);
        Assert.Equal("Tu enlace para entrar a Paquetenvia", mail.Subject);
        Assert.Contains("El enlace vence en 7 minutos y sólo funciona una vez.", mail.Text);
        Assert.Contains("El enlace vence en 7 minutos y sólo funciona una vez.", mail.Html);
        Assert.Contains("Si no fuiste tú, ignora este mensaje; nadie podrá entrar a tu cuenta sin este enlace.", mail.Text);
    }

    [Fact]
    public async Task EmailedCodes_StateTheirRealLifetime_AndSignInCodesComeFromTheApplication()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateBrandedApplicationAsync(admin, "Paquetenvia");
        var email = await CreateUserAsync(application.Code);
        Guid userId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            Assert.True((await scope.ServiceProvider.GetRequiredService<IMfaService>().SetupEmailOtpAsync(userId)).IsSuccess);
        }

        // Setting up the factor (from the account portal): the setup code works ten minutes.
        var setup = await LatestAsync(email, "mfa-otp");
        Assert.Equal(10, setup.ValidMinutes);
        Assert.Null(setup.ApplicationDisplayName);
        var setupMail = Render(setup);
        Assert.Equal("Tu código de verificación", setupMail.Subject);
        Assert.Equal(setup.Secret, Regex.Match(setupMail.Html, @">\s*(\d{6})\s*<").Groups[1].Value);
        Assert.Contains("El código vence en 10 minutos.", setupMail.Text);

        // Signing in: the code works as long as the sign-in step (Mfa:MfaTokenExpirySeconds = 300).
        string pendingToken;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<IMfaService>().EnableEmailOtpAsync(userId, new EnableEmailMfaRequest { Code = setup.Secret })).IsSuccess);
            pendingToken = scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateMfaPendingToken(userId, application.Code);
        }
        var sent = await _factory.CreateAuthCenterClient().PostAsJsonAsync("/api/auth/mfa/email-otp/send", new SendMfaEmailOtpRequest { MfaPendingToken = pendingToken });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        var signIn = await LatestAsync(email, "mfa-otp");
        Assert.Equal(5, signIn.ValidMinutes);
        Assert.Equal("Paquetenvia", signIn.ApplicationDisplayName);
        var signInMail = Render(signIn);
        Assert.Equal("Paquetenvia", signInMail.FromName);
        Assert.Contains("Escribe este código para continuar en Paquetenvia:", signInMail.Text);
        Assert.Contains("El código vence en 5 minutos.", signInMail.Text);
    }

    [Fact]
    public async Task SecurityNotices_AreSpanish_AndSayWhatToDoIfItWasNotYou()
    {
        var email = await CreateUserAsync(DomainConstants.SystemCodes.AuthCenter);
        using var client = await SignedInClientAsync(email);

        var changed = await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest { CurrentPassword = Password, NewPassword = TestSecretGenerator.CreatePassword() });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var notice = await LatestAsync(email, "security-notice");
        Assert.Equal("Se cambió tu contraseña", notice.ApplicationName);
        var mail = Render(notice);
        Assert.Equal("Security notice", mail.Purpose);
        Assert.Equal("Se cambió tu contraseña", mail.Subject);
        Assert.Contains("Se cambió la contraseña de tu cuenta.", mail.Text);
        Assert.Contains("Si no reconoces esta actividad, cambia tu contraseña de inmediato.", mail.Text);
        Assert.Contains("Si no reconoces esta actividad, cambia tu contraseña de inmediato.", mail.Html);
        Assert.DoesNotContain("authcenter", Words(mail.Html), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignUpAttemptNotice_ComesFromTheApplicationSignedUpTo()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateBrandedApplicationAsync(admin, "Paquetenvia");
        var email = await CreateUserAsync(application.Code);

        // The hosted sign-up answers as for a new account and tells the owner instead.
        using var browser = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(HttpsAuthCenterFactory.Authority), HandleCookies = true });
        var attempt = await browser.PostAsJsonAsync("/ui-api/session/register", new RegisterRequest
        {
            FullName = "Otra Persona", Email = email, Password = TestSecretGenerator.CreatePassword(), ApplicationCode = application.Code
        });

        Assert.Equal(HttpStatusCode.OK, attempt.StatusCode);

        var notice = await LatestAsync(email, "security-notice");
        Assert.Equal("Intentaron crear una cuenta con tu correo", notice.ApplicationName);
        Assert.Equal("Paquetenvia", notice.ApplicationDisplayName);
        var mail = Render(notice);
        Assert.Equal("Paquetenvia", mail.FromName);
        Assert.Contains("ya tienes una cuenta", mail.Html);
        Assert.Contains($"Paquetenvia · Ayuda: {SupportUrl}", mail.Text);
    }

    [Fact]
    public void PayloadsQueuedBeforeTheUpgrade_StillDeserialize_AndAreSent()
    {
        const string invitation = """{"Kind":"invitation","ToEmail":"ana@example.com","ToName":"Ana","Secret":"token","ActionUrl":"https://login.example/accept-invitation?application=SHOP","ApplicationName":"Tienda","ActionLabel":null}""";
        const string code = """{"Kind":"mfa-otp","ToEmail":"ana@example.com","ToName":"Ana","Secret":"042913","ActionUrl":null,"ApplicationName":null,"ActionLabel":null}""";

        var queuedInvitation = JsonSerializer.Deserialize<OutboxEmailService.EmailPayload>(invitation)!;
        var queuedCode = JsonSerializer.Deserialize<OutboxEmailService.EmailPayload>(code)!;

        Assert.Null(queuedInvitation.ApplicationDisplayName);
        Assert.Null(queuedInvitation.ApplicationSupportUrl);
        Assert.Null(queuedCode.ValidMinutes);
        Assert.Equal("Te invitaron a Tienda", Render(queuedInvitation).Subject);
        Assert.Contains("El código vence en unos minutos.", Render(queuedCode).Text);
    }

    private static readonly string Password = TestSecretGenerator.CreatePassword();

    // Links can carry an application code or a domain the operator chose; only the words count.
    private static string Words(string content) => Regex.Replace(content, @"https?://[^\s""<]+", string.Empty);

    private static string Read(AlternateView view)
    {
        view.ContentStream.Position = 0;
        return new StreamReader(view.ContentStream).ReadToEnd();
    }

    private SmtpEmailService.RenderedEmail Render(OutboxEmailService.EmailPayload payload)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SmtpEmailService>().Render(payload);
    }

    private async Task<OutboxEmailService.EmailPayload> LatestAsync(string to, string kind)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("AuthCenter.Outbox.Email.v1");
        var messages = await db.OutboxMessages.AsNoTracking().Where(message => message.Type == "email.v1").OrderBy(message => message.CreatedAt).ToListAsync();
        return messages
            .Select(message => JsonSerializer.Deserialize<OutboxEmailService.EmailPayload>(protector.Unprotect(message.ProtectedPayload))!)
            .Last(mail => mail.Kind == kind && string.Equals(mail.ToEmail, to, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An active user with access to the application, whose password is <see cref="Password"/>.</summary>
    private async Task<string> CreateUserAsync(string applicationCode)
    {
        var email = $"mail-{Guid.NewGuid():N}@example.com";
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), FullName = "Ana Muñoz", Email = email, UserName = email, EmailConfirmed = true,
            HasLocalPassword = true, IsActive = true, CreatedAt = DateTime.UtcNow
        };
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, Password)).Succeeded);
        var applicationId = await db.ApplicationSystems.Where(item => item.Code == applicationCode).Select(item => item.Id).SingleAsync();
        db.UserApplicationAccesses.Add(new UserApplicationAccess { Id = Guid.NewGuid(), UserId = user.Id, ApplicationSystemId = applicationId, IsActive = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return email;
    }

    /// <summary>An application whose brand (display name and support link) differs from its internal name.</summary>
    private static async Task<(Guid Id, string Code)> CreateBrandedApplicationAsync(HttpClient admin, string displayName, bool magicLink = false)
    {
        var code = "MAIL" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await admin.PostAsJsonAsync("/api/applications", new
        {
            Code = code,
            Name = "Interna " + code,
            RegistrationMode = "Open",
            AllowPasswordLogin = true,
            AllowMagicLink = magicLink
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetGuid();
        var branded = await admin.PutAsJsonAsync($"/api/applications/{id}/branding", new UpdateApplicationBrandingRequest { DisplayName = displayName, SupportUrl = SupportUrl });
        Assert.Equal(HttpStatusCode.OK, branded.StatusCode);
        return (id, code);
    }

    private async Task<HttpClient> SignedInClientAsync(string email)
    {
        var client = _factory.CreateAuthCenterClient();
        var auth = (await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = Password, ApplicationCode = DomainConstants.SystemCodes.AuthCenter }))
            .Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateAuthCenterClient();
        var auth = (await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        })).Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }
}
