using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// The account features behind the hosted login and portal: the pages emailed links open, sign-in
/// options, emailed sign-in links, factor enrollment during sign-in, factor management and the
/// security notices sent for account changes.
/// </summary>
public sealed class HostedAccountTests : IClassFixture<HttpsAuthCenterFactory>
{
    private readonly HttpsAuthCenterFactory _factory;

    public HostedAccountTests(HttpsAuthCenterFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/reset-password", "/assets/account.js")]
    [InlineData("/accept-invitation", "/assets/account.js")]
    [InlineData("/confirm-email", "/assets/account.js")]
    [InlineData("/confirm-email-change", "/assets/account.js")]
    [InlineData("/magic-link", "/assets/login.js")]
    public async Task EmailedLinkPages_AreServedWithoutLeakingTheirTokens(string path, string script)
    {
        using var browser = CreateBrowser();

        var response = await browser.GetAsync($"{path}?token=single-use&email=ana%40example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(script, await response.Content.ReadAsStringAsync());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task LoginOptions_DescribeHowAnApplicationSignsIn()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin, magicLink: true, password: false);
        using var browser = CreateBrowser();

        var options = await ReadDataAsync(await browser.GetAsync($"/ui-api/session/login-options?applicationCode={application.Code}"));

        Assert.Equal(application.Code, options.GetProperty("applicationCode").GetString());
        Assert.False(options.GetProperty("allowPasswordLogin").GetBoolean());
        Assert.True(options.GetProperty("allowMagicLink").GetBoolean());
        Assert.False(options.GetProperty("federationAvailable").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/ui-api/session/login-options?applicationCode=NOPE")).StatusCode);
    }

    [Fact]
    public async Task EmailedSignInLink_SignsTheBrowserInOnce_ForTheApplicationItWasIssuedFor()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin, magicLink: true);
        var (email, _) = await RegisterAsync(application.Code);
        using var browser = CreateBrowser();

        var request = await browser.PostAsJsonAsync("/api/auth/magic-link/request", new MagicLinkRequest { Email = email, ApplicationCode = application.Code });
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        var mail = await LatestMailAsync(email, "magic-link");
        // The hosted page is branded for the application and receives the token in the link.
        Assert.Contains($"application={application.Code}", mail.ActionUrl);

        var signedIn = await ReadDataAsync(await browser.PostAsJsonAsync("/ui-api/session/magic-link", new { token = mail.Secret }));
        Assert.Equal(email, signedIn.GetProperty("user").GetProperty("email").GetString());
        var session = await ReadDataAsync(await browser.GetAsync("/ui-api/session"));
        Assert.Contains(application.Code, session.GetProperty("user").GetProperty("applications").EnumerateArray().Select(item => item.GetString()));

        var replay = await PostAsync(browser, "/ui-api/session/magic-link", new { token = mail.Secret }, session.GetProperty("csrfToken").GetString());
        Assert.Equal("TOKEN_ALREADY_USED", await ErrorCodeAsync(replay));
    }

    [Fact]
    public async Task RegistrationInAnApplicationRequiringMfa_KeepsTheAccount_AndTheHostedLoginEnrollsTheFactor()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin, requireMfa: true);
        var email = $"mfa-signup-{Guid.NewGuid():N}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        using var api = _factory.CreateAuthCenterClient();

        var registered = await api.PostAsJsonAsync("/api/auth/register", new RegisterRequest { FullName = "Ana", Email = email, Password = password, ApplicationCode = application.Code });

        // JSON clients get a fixed explanation, never the enrollment token.
        Assert.Equal(HttpStatusCode.BadRequest, registered.StatusCode);
        var body = await registered.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("MFA_SETUP_REQUIRED", body!.ErrorCode);
        Assert.StartsWith("This application requires MFA.", body.Message);
        var login = await api.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password, ApplicationCode = application.Code });
        Assert.StartsWith("This application requires MFA.", (await login.Content.ReadFromJsonAsync<ApiResponse<object>>())!.Message);

        using var browser = CreateBrowser();
        var step = await ReadDataAsync(await browser.PostAsJsonAsync("/ui-api/session/login", new LoginRequest { Email = email, Password = password, ApplicationCode = application.Code }));
        Assert.True(step.GetProperty("requiresMfaEnrollment").GetBoolean());
        var enrollmentToken = step.GetProperty("enrollmentToken").GetString()!;
        var setup = await ReadDataAsync(await browser.PostAsJsonAsync("/ui-api/session/mfa/enrollment/start", new TotpEnrollmentRequest { EnrollmentToken = enrollmentToken }));
        Assert.StartsWith("otpauth://totp/", setup.GetProperty("totpUri").GetString());
        var enrolled = await ReadDataAsync(await browser.PostAsJsonAsync("/ui-api/session/mfa/enrollment/complete", new TotpEnrollmentRequest
        {
            EnrollmentToken = enrollmentToken,
            TotpCode = TestTotp.Code(setup.GetProperty("secretBase32").GetString()!)
        }));
        Assert.Equal(email, enrolled.GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/ui-api/session")).StatusCode);
        Assert.Contains(await OutboxAsync(), mail => mail.Kind == "security-notice" && mail.ToEmail == email);
    }

    [Theory]
    [InlineData("Open", true, true)]
    [InlineData("ApprovalRequired", true, true)]
    [InlineData("InviteOnly", true, false)]
    [InlineData("Closed", true, false)]
    [InlineData("Open", false, false)]
    public async Task SignUp_IsOffered_WhereAnyoneMayCreateAPasswordAccount(string registrationMode, bool password, bool offered)
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin, password: password, registrationMode: registrationMode);
        using var browser = CreateBrowser();

        var options = await ReadDataAsync(await browser.GetAsync($"/ui-api/session/login-options?applicationCode={application.Code}"));
        Assert.Equal(offered, options.GetProperty("allowSelfRegistration").GetBoolean());

        if (!offered)
        {
            var refused = await browser.PostAsJsonAsync("/ui-api/session/register", new RegisterRequest
            {
                FullName = "Ana", Email = $"closed-{Guid.NewGuid():N}@example.com", Password = TestSecretGenerator.CreatePassword(), ApplicationCode = application.Code
            });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(password ? "REGISTRATION_CLOSED" : "PASSWORD_LOGIN_DISABLED", await ErrorCodeAsync(refused));
        }
    }

    [Theory]
    [InlineData("Open", null, "Consumers")]
    [InlineData("ApprovalRequired", null, "Employees")]
    [InlineData("InviteOnly", null, "Employees")]
    [InlineData("Open", "Employees", "Employees")]
    [InlineData("Closed", "Consumers", "Consumers")]
    public async Task Audience_DefaultsFromTheRegistrationMode_AndReachesTheLogin(string registrationMode, string? audience, string expected)
    {
        using var admin = await CreateAdminClientAsync();
        var code = "AUD" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await ReadDataAsync(await admin.PostAsJsonAsync("/api/applications", new
        {
            Code = code,
            Name = "Audience " + code,
            RegistrationMode = registrationMode,
            Audience = audience,
            AllowPasswordLogin = true
        }));
        Assert.Equal(expected, created.GetProperty("registrationSettings").GetProperty("audience").GetString());

        using var browser = CreateBrowser();
        var options = await ReadDataAsync(await browser.GetAsync($"/ui-api/session/login-options?applicationCode={code}"));
        Assert.Equal(expected, options.GetProperty("audience").GetString());
    }

    [Fact]
    public async Task Audience_IsKeptWhenAnUpdateLeavesItOut_AndRejectsUnknownValues()
    {
        using var admin = await CreateAdminClientAsync();
        var (id, code) = await CreateApplicationAsync(admin, registrationMode: "Open");

        object Update(string? audience) => new { Name = "Hosted " + code, RegistrationMode = "Closed", AllowPasswordLogin = true, Audience = audience };
        var kept = await ReadDataAsync(await admin.PutAsJsonAsync($"/api/applications/{id}", Update(null)));
        Assert.Equal("Consumers", kept.GetProperty("registrationSettings").GetProperty("audience").GetString());

        var changed = await ReadDataAsync(await admin.PutAsJsonAsync($"/api/applications/{id}", Update("Employees")));
        Assert.Equal("Employees", changed.GetProperty("registrationSettings").GetProperty("audience").GetString());

        var unknown = await admin.PutAsJsonAsync($"/api/applications/{id}", Update("1"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("INVALID_AUDIENCE", await ErrorCodeAsync(unknown));

        var refused = await admin.PostAsJsonAsync("/api/applications", new { Code = "AUDX" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), Name = "X", RegistrationMode = "Open", Audience = "Everyone" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task PublicBranding_NamesNoProductForTheUnbrandedSystemApplication()
    {
        using var admin = await CreateAdminClientAsync();
        var (_, code) = await CreateApplicationAsync(admin);
        using var browser = CreateBrowser();

        // End users only know the applications: the system one has no name until it is branded.
        var system = await ReadDataAsync(await browser.GetAsync($"/api/applications/branding/{DomainConstants.SystemCodes.AuthCenter}"));
        Assert.Equal(string.Empty, system.GetProperty("displayName").GetString());
        Assert.Equal("#2563EB", system.GetProperty("primaryColor").GetString());

        var application = await ReadDataAsync(await browser.GetAsync($"/api/applications/branding/{code}"));
        Assert.Equal("Hosted " + code, application.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task SignUp_WhereTheEmailMustBeConfirmed_SignsInOnlyAfterTheLinkIsFollowed()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin, requireEmailConfirmation: true);
        var email = $"signup-{Guid.NewGuid():N}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        using var browser = CreateBrowser();

        var registered = await ReadDataAsync(await browser.PostAsJsonAsync("/ui-api/session/register", new RegisterRequest
        {
            FullName = "Ana Nueva", Email = email, Password = password, ApplicationCode = application.Code
        }));

        Assert.True(registered.GetProperty("confirmationRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/ui-api/session")).StatusCode);
        var confirmation = await LatestMailAsync(email, "email-confirmation");
        Assert.Contains($"application={application.Code}", confirmation.ActionUrl);
        var early = await browser.PostAsJsonAsync("/ui-api/session/login", new LoginRequest { Email = email, Password = password, ApplicationCode = application.Code });
        Assert.Equal("EMAIL_NOT_CONFIRMED", await ErrorCodeAsync(early));

        using var api = _factory.CreateAuthCenterClient();
        var confirmed = await api.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmailRequest { Email = email, Token = confirmation.Secret });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var signedIn = await ReadDataAsync(await browser.PostAsJsonAsync("/ui-api/session/login", new LoginRequest { Email = email, Password = password, ApplicationCode = application.Code }));
        Assert.Equal(email, signedIn.GetProperty("user").GetProperty("email").GetString());
    }

    [Fact]
    public async Task SignUp_WhereConfirmationIsOptional_SignsTheBrowserInAtOnce()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var email = $"signup-now-{Guid.NewGuid():N}@example.com";
        using var browser = CreateBrowser();

        var signedIn = await ReadDataAsync(await browser.PostAsJsonAsync("/ui-api/session/register", new RegisterRequest
        {
            FullName = "Ana Inmediata", Email = email, Password = TestSecretGenerator.CreatePassword(), ApplicationCode = application.Code
        }));

        Assert.Equal(email, signedIn.GetProperty("user").GetProperty("email").GetString());
        var session = await ReadDataAsync(await browser.GetAsync("/ui-api/session"));
        Assert.Contains(application.Code, session.GetProperty("user").GetProperty("applications").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task SignUp_WithTheEmailOfAnAccount_AnswersLikeANewAccount_AndOnlyWarnsItsOwner()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin, requireEmailConfirmation: true);
        var (email, password) = await RegisterAsync(application.Code, admin: true);
        using var browser = CreateBrowser();

        var attempt = await ReadDataAsync(await browser.PostAsJsonAsync("/ui-api/session/register", new RegisterRequest
        {
            FullName = "Someone Else", Email = email, Password = TestSecretGenerator.CreatePassword(), ApplicationCode = application.Code
        }));

        // The same answer as a new account that must confirm its email: the form reveals no account.
        Assert.True(attempt.GetProperty("confirmationRequired").GetBoolean());
        Assert.Contains(await OutboxAsync(), mail => mail.Kind == "security-notice" && mail.ToEmail == email && mail.ApplicationName == "Sign-up attempt with your email");
        Assert.DoesNotContain(await OutboxAsync(), mail => mail.Kind == "email-confirmation" && mail.ToEmail == email);
        // The account is untouched: its own password still signs in.
        var signedIn = await ReadDataAsync(await browser.PostAsJsonAsync("/ui-api/session/login", new LoginRequest { Email = email, Password = password, ApplicationCode = application.Code }));
        Assert.Equal(email, signedIn.GetProperty("user").GetProperty("email").GetString());
    }

    [Fact]
    public async Task SignUp_WhereAccessNeedsApproval_CreatesTheAccountWithoutSigningIn()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin, registrationMode: "ApprovalRequired");
        using var browser = CreateBrowser();

        var registered = await ReadDataAsync(await browser.PostAsJsonAsync("/ui-api/session/register", new RegisterRequest
        {
            FullName = "Ana Pendiente", Email = $"signup-approval-{Guid.NewGuid():N}@example.com", Password = TestSecretGenerator.CreatePassword(), ApplicationCode = application.Code
        }));

        Assert.True(registered.GetProperty("approvalRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/ui-api/session")).StatusCode);

        // An address with an account gets the same answer as the new one did.
        var (email, _) = await RegisterAsync(application.Code, admin: true);
        var attempt = await ReadDataAsync(await browser.PostAsJsonAsync("/ui-api/session/register", new RegisterRequest
        {
            FullName = "Someone Else", Email = email, Password = TestSecretGenerator.CreatePassword(), ApplicationCode = application.Code
        }));
        Assert.True(attempt.GetProperty("approvalRequired").GetBoolean());
        Assert.Contains(await OutboxAsync(), mail => mail.Kind == "security-notice" && mail.ToEmail == email);
    }

    [Fact]
    public async Task PasskeyEnrollment_NeedsAValidEnrollmentToken()
    {
        using var browser = CreateBrowser();

        var options = await browser.PostAsJsonAsync("/ui-api/session/passkey/enrollment/options", new PasskeyEnrollmentRequest { EnrollmentToken = "not-a-token" });

        Assert.Equal(HttpStatusCode.BadRequest, options.StatusCode);
        Assert.Equal("INVALID_ENROLLMENT", await ErrorCodeAsync(options));
    }

    [Fact]
    public async Task AuthenticatorCode_IsAcceptedOnlyOnce()
    {
        var (email, password) = await RegisterAsync(DomainConstants.SystemCodes.AuthCenter, admin: true);
        using var client = await SignedInClientAsync(email, password);
        await client.AddReauthenticationProofAsync(password, "factor.enroll");
        var setup = (await (await client.PostAsync("/api/auth/mfa/setup", null)).Content.ReadFromJsonAsync<ApiResponse<MfaSetupResponse>>())!.Data!;
        var code = TestTotp.Code(setup.SecretBase32);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/mfa/enable", new EnableMfaRequest { TotpCode = code })).StatusCode);

        using var anonymous = _factory.CreateAuthCenterClient();
        var pending = (await (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password, ApplicationCode = DomainConstants.SystemCodes.AuthCenter }))
            .Content.ReadFromJsonAsync<ApiResponse<MfaPendingResponse>>())!.Data!;
        var replayed = await anonymous.PostAsJsonAsync("/api/auth/mfa/verify", new VerifyMfaRequest { MfaPendingToken = pending.MfaPendingToken, TotpCode = code });
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Equal("INVALID_MFA_CODE", await ErrorCodeAsync(replayed));

        var fresh = await anonymous.PostAsJsonAsync("/api/auth/mfa/verify", new VerifyMfaRequest { MfaPendingToken = pending.MfaPendingToken, TotpCode = TestTotp.Code(setup.SecretBase32) });
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task EmailFactor_IsDisabledWithACodeSentForThatPurpose()
    {
        var (email, password) = await RegisterAsync(DomainConstants.SystemCodes.AuthCenter, admin: true);
        using var client = await SignedInClientAsync(email, password);
        await client.AddReauthenticationProofAsync(password, "factor.enroll");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/mfa/email-otp/setup", null)).StatusCode);
        var setupCode = (await LatestMailAsync(email, "mfa-otp")).Secret;
        var enabled = await client.PostAsJsonAsync("/api/auth/mfa/email-otp/enable", new EnableEmailMfaRequest { Code = setupCode });
        Assert.True(enabled.IsSuccessStatusCode, await enabled.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/mfa/email-otp/verification", null)).StatusCode);
        var code = (await LatestMailAsync(email, "mfa-otp")).Secret;
        using var disable = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/mfa") { Content = JsonContent.Create(new DisableMfaRequest { EmailOtpCode = code }) };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(disable)).StatusCode);

        var status = (await (await client.GetAsync("/api/auth/mfa/status")).Content.ReadFromJsonAsync<ApiResponse<MfaStatusDto>>())!.Data!;
        Assert.False(status.IsEnabled);
        Assert.Contains(await OutboxAsync(), mail => mail.Kind == "security-notice" && mail.ToEmail == email && mail.ApplicationName == "Two-step verification disabled");
        // Without an enabled email factor there is nothing to confirm.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/mfa/email-otp/verification", null)).StatusCode);
    }

    [Fact]
    public async Task EmailChange_LinkNamesTheAccount_AndBothAddressesAreTold()
    {
        var (email, password) = await RegisterAsync(DomainConstants.SystemCodes.AuthCenter, admin: true);
        using var client = await SignedInClientAsync(email, password);
        var newEmail = $"changed-{Guid.NewGuid():N}@example.com";
        await client.AddReauthenticationProofAsync(password, "account.change-email");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/email-change/request", new RequestEmailChangeRequest { NewEmail = newEmail })).StatusCode);

        var confirmation = await LatestMailAsync(newEmail, "email-change");
        var userId = QueryHelpers.ParseQuery(new Uri(confirmation.ActionUrl!).Query)["userId"].ToString();
        Assert.True(Guid.TryParse(userId, out _));
        Assert.Contains(await OutboxAsync(), mail => mail.Kind == "security-notice" && mail.ToEmail == email && mail.ApplicationName == "Email change requested");
        var confirmed = await client.PostAsJsonAsync("/api/auth/email-change/confirm", new ConfirmEmailChangeRequest { UserId = Guid.Parse(userId), NewEmail = newEmail, Token = confirmation.Secret });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Contains(await OutboxAsync(), mail => mail.Kind == "security-notice" && mail.ToEmail == email && mail.ApplicationName == "Email address changed");
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateAuthCenterClient().PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = newEmail, Password = password, ApplicationCode = DomainConstants.SystemCodes.AuthCenter })).StatusCode);
    }

    [Fact]
    public async Task PasswordChange_TellsTheOwner_AndResetLinksNameTheApplication()
    {
        var (email, password) = await RegisterAsync(DomainConstants.SystemCodes.AuthCenter, admin: true);
        using var client = await SignedInClientAsync(email, password);

        var changed = await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest { CurrentPassword = password, NewPassword = TestSecretGenerator.CreatePassword() });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Contains(await OutboxAsync(), mail => mail.Kind == "security-notice" && mail.ToEmail == email && mail.ApplicationName == "Password changed");

        await _factory.CreateAuthCenterClient().PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest { Email = email, ApplicationCode = DomainConstants.SystemCodes.AuthCenter });
        Assert.Contains($"application={DomainConstants.SystemCodes.AuthCenter}", (await LatestMailAsync(email, "password-reset")).ActionUrl);
    }

    [Fact]
    public async Task Applications_ListWhatTheUserCanUse()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var (email, password) = await RegisterAsync(application.Code);
        using var client = _factory.CreateAuthCenterClient();
        var auth = (await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password, ApplicationCode = application.Code }))
            .Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var applications = await ReadDataAsync(await client.GetAsync("/api/auth/applications"));

        var item = Assert.Single(applications.EnumerateArray());
        Assert.Equal(application.Code, item.GetProperty("code").GetString());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("grantedAt").ValueKind);
    }

    [Fact]
    public async Task ConcurrentSignInsOfOneAccount_AreBothRecorded_WithoutFailingTheRequest()
    {
        var (email, _) = await RegisterAsync(DomainConstants.SystemCodes.AuthCenter, admin: true);
        await using var first = _factory.Services.CreateAsyncScope();
        await using var second = _factory.Services.CreateAsyncScope();
        var firstUsers = first.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AuthCenter.Domain.Entities.ApplicationUser>>();
        var secondUsers = second.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AuthCenter.Domain.Entities.ApplicationUser>>();
        var secondDb = second.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        // Both requests loaded the account (and its concurrency stamp) before either saved it.
        var loadedByFirst = (await firstUsers.FindByEmailAsync(email))!;
        var loadedBySecond = (await secondUsers.FindByEmailAsync(email))!;
        var later = DateTime.UtcNow.AddMinutes(1);

        await firstUsers.RecordSignInAsync(first.ServiceProvider.GetRequiredService<AuthCenterDbContext>(), loadedByFirst, DateTime.UtcNow, CancellationToken.None);
        await secondUsers.RecordSignInAsync(secondDb, loadedBySecond, later, CancellationToken.None);

        // Nothing stale is left for the next save of the second request (its session, for example).
        Assert.Equal(0, await secondDb.SaveChangesAsync());
        await using var check = _factory.Services.CreateAsyncScope();
        var stored = await check.ServiceProvider.GetRequiredService<AuthCenterDbContext>().Users.AsNoTracking().SingleAsync(user => user.Email == email);
        Assert.Equal(later, stored.LastLoginAt);
    }

    private async Task<IReadOnlyList<OutboxMail>> OutboxAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await OutboxMail.ReadAsync(scope.ServiceProvider);
    }

    private async Task<OutboxMail> LatestMailAsync(string to, string kind) =>
        (await OutboxAsync()).Last(mail => mail.Kind == kind && string.Equals(mail.ToEmail, to, StringComparison.OrdinalIgnoreCase));

    private async Task<HttpClient> SignedInClientAsync(string email, string password)
    {
        var client = _factory.CreateAuthCenterClient();
        var auth = (await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password, ApplicationCode = DomainConstants.SystemCodes.AuthCenter }))
            .Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    /// <summary>A self-registered user; <paramref name="admin"/> grants AuthCenter access directly instead.</summary>
    private async Task<(string Email, string Password)> RegisterAsync(string applicationCode, bool admin = false)
    {
        var email = $"hosted-{Guid.NewGuid():N}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        if (admin)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AuthCenter.Domain.Entities.ApplicationUser>>();
            var user = new AuthCenter.Domain.Entities.ApplicationUser
            {
                Id = Guid.NewGuid(), FullName = "Hosted User", Email = email, UserName = email, EmailConfirmed = true,
                HasLocalPassword = true, IsActive = true, CreatedAt = DateTime.UtcNow
            };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            var applicationId = await db.ApplicationSystems.Where(item => item.Code == applicationCode).Select(item => item.Id).SingleAsync();
            db.UserApplicationAccesses.Add(new AuthCenter.Domain.Entities.UserApplicationAccess { Id = Guid.NewGuid(), UserId = user.Id, ApplicationSystemId = applicationId, IsActive = true, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return (email, password);
        }
        using var client = _factory.CreateAuthCenterClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest { FullName = "Hosted User", Email = email, Password = password, ApplicationCode = applicationCode });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (email, password);
    }

    private static async Task<(Guid Id, string Code)> CreateApplicationAsync(
        HttpClient admin,
        bool requireMfa = false,
        bool magicLink = false,
        bool password = true,
        string registrationMode = "Open",
        bool requireEmailConfirmation = false)
    {
        var code = "HOSTED" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await ReadDataAsync(await admin.PostAsJsonAsync("/api/applications", new
        {
            Code = code,
            Name = "Hosted " + code,
            RegistrationMode = registrationMode,
            AllowPasswordLogin = password,
            AllowMagicLink = magicLink,
            RequireMfa = requireMfa,
            RequireEmailConfirmation = requireEmailConfirmation
        }));
        return (created.GetProperty("id").GetGuid(), code);
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

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(HttpsAuthCenterFactory.Authority),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string path, object body, string? csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (csrf is not null)
            request.Headers.Add("X-AuthCenter-CSRF", csrf);
        return await browser.SendAsync(request);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode;

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("data").Clone();
    }
}
