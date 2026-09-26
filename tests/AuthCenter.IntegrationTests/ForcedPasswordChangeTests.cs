using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// Regressions for the temporary-password path: the hosted login must be able to finish it, and
/// replacing the password must never skip the application's access-policy and MFA gate.
/// </summary>
public sealed class ForcedPasswordChangeTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public ForcedPasswordChangeTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task HostedLogin_TemporaryPassword_CompletesAfterMandatoryChange()
    {
        var email = $"temporary-ui-{Guid.NewGuid():N}@example.com";
        var temporaryPassword = TestSecretGenerator.CreatePassword();
        await CreateUserAsync(email, temporaryPassword, mustChangePassword: true);
        using var browser = CreateBrowser();

        var login = await browser.PostAsJsonAsync("/ui-api/session/login", new LoginRequest
        {
            Email = email,
            Password = temporaryPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var step = await ReadDataAsync(login);
        Assert.True(step.GetProperty("requiresPasswordChange").GetBoolean());
        var changeToken = step.GetProperty("passwordChangeToken").GetString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/ui-api/session")).StatusCode);

        var change = await browser.PostAsJsonAsync("/ui-api/session/forced-change", new ForcedChangePasswordRequest
        {
            ForcedChangePendingToken = changeToken!,
            NewPassword = TestSecretGenerator.CreatePassword()
        });

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await ReadDataAsync(change)).GetProperty("csrfToken").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/ui-api/session")).StatusCode);
    }

    [Fact]
    public async Task ForcedChange_WithEnabledMfa_StillRequiresTheSecondFactor()
    {
        var email = $"temporary-mfa-{Guid.NewGuid():N}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        var userId = await CreateUserAsync(email, password, mustChangePassword: false);
        using var client = _factory.CreateClient();
        var secret = await EnableTotpAsync(client, email, password);
        await SetMustChangePasswordAsync(userId);
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Remove("X-AuthCenter-Reauthentication");

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        var pendingChange = (await login.Content.ReadFromJsonAsync<ApiResponse<ForcedChangePendingResponse>>())!.Data!;
        Assert.True(pendingChange.PasswordChangeRequired);

        var change = await client.PostAsJsonAsync("/api/auth/forced-change-password", new ForcedChangePasswordRequest
        {
            ForcedChangePendingToken = pendingChange.ForcedChangePendingToken,
            NewPassword = TestSecretGenerator.CreatePassword()
        });

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var raw = await change.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accessToken", raw, StringComparison.OrdinalIgnoreCase);
        var mfaPending = JsonSerializer.Deserialize<ApiResponse<MfaPendingResponse>>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Data!;
        Assert.True(mfaPending.MfaRequired);

        var verify = await client.PostAsJsonAsync("/api/auth/mfa/verify", new VerifyMfaRequest
        {
            MfaPendingToken = mfaPending.MfaPendingToken,
            TotpCode = TestTotp.Code(secret)
        });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var auth = await verify.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>();
        Assert.False(string.IsNullOrWhiteSpace(auth?.Data?.AccessToken));
    }

    [Fact]
    public async Task PasswordLogin_DisabledForApplication_IsRejectedWithoutRevealingPasswordValidity()
    {
        using var admin = await CreateAdminClientAsync();
        var code = "FEDONLY" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await admin.PostAsJsonAsync("/api/applications", new
        {
            Code = code,
            Name = "Federation only " + code,
            RegistrationMode = "Closed",
            AllowPasswordLogin = false
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var email = $"federated-only-{Guid.NewGuid():N}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        await CreateUserAsync(email, password, mustChangePassword: false, applicationCode: code);

        foreach (var candidate in new[] { password, TestSecretGenerator.CreatePassword() })
        {
            using var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
            {
                Email = email,
                Password = candidate,
                ApplicationCode = code
            });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("PASSWORD_LOGIN_DISABLED", (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);
        }
    }

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true
    });

    private async Task<string> EnableTotpAsync(HttpClient client, string email, string password)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        var auth = (await login.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        await client.AddReauthenticationProofAsync(password, "factor.enroll");
        var setup = (await (await client.PostAsync("/api/auth/mfa/setup", null)).Content.ReadFromJsonAsync<ApiResponse<MfaSetupResponse>>())!.Data!;
        var enable = await client.PostAsJsonAsync("/api/auth/mfa/enable", new EnableMfaRequest
        {
            TotpCode = TestTotp.Code(setup.SecretBase32)
        });
        enable.EnsureSuccessStatusCode();
        return setup.SecretBase32;
    }

    private async Task SetMustChangePasswordAsync(Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId.ToString());
        user!.MustChangePassword = true;
        await users.UpdateAsync(user);
    }

    private async Task<Guid> CreateUserAsync(
        string email,
        string password,
        bool mustChangePassword,
        string applicationCode = DomainConstants.SystemCodes.AuthCenter)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var application = await db.ApplicationSystems.SingleAsync(item => item.Code == applicationCode);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = "Temporary Password User",
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            HasLocalPassword = true,
            MustChangePassword = mustChangePassword,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
        db.UserApplicationAccesses.Add(new UserApplicationAccess
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ApplicationSystemId = application.Id,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        var auth = (await login.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
