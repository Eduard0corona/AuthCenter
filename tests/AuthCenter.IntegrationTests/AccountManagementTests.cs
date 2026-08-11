using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// Covers the self-service surface a signed-in user has over their own account: sessions, trusted
/// devices, linked providers, email address and deletion. These endpoints act on credentials, so
/// each one is also checked for the case where the caller aims it at somebody else's data.
/// </summary>
public class AccountManagementTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public AccountManagementTests(AuthCenterWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // --- Sessions ---

    [Fact]
    public async Task Sessions_AreListedPerLogin_AndRevokingOneKillsOnlyItsRefreshToken()
    {
        var (email, password) = await CreateUserAsync();
        using var client = _factory.CreateClient();

        var first = await LoginAsync(client, email, password);
        var second = await LoginAsync(client, email, password);

        Authorize(client, second.AccessToken);
        var sessions = await ReadDataAsync<List<SessionDto>>(await client.GetAsync("/api/auth/sessions"));
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, session => Assert.Equal("AUTHCENTER", session.ApplicationCode));

        var firstSessionId = await GetSessionIdAsync(first.RefreshToken);
        var revokeResponse = await client.DeleteAsync($"/api/auth/sessions/{firstSessionId}");
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        var remaining = await ReadDataAsync<List<SessionDto>>(await client.GetAsync("/api/auth/sessions"));
        Assert.Equal(await GetSessionIdAsync(second.RefreshToken), Assert.Single(remaining).Id);

        // Checked before the revoked token is presented: doing so trips reuse detection, which
        // deliberately takes down every session for the application.
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(second.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(first.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task PresentingARevokedRefreshToken_TakesDownEverySessionForThatApplication()
    {
        var (email, password) = await CreateUserAsync();
        using var client = _factory.CreateClient();

        var revoked = await LoginAsync(client, email, password);
        var untouched = await LoginAsync(client, email, password);

        Authorize(client, untouched.AccessToken);
        var revokedSessionId = await GetSessionIdAsync(revoked.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/auth/sessions/{revokedSessionId}")).StatusCode);

        var reuseResponse = await RefreshAsync(revoked.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, reuseResponse.StatusCode);
        Assert.Equal("TOKEN_REUSE_DETECTED", await ReadErrorCodeAsync(reuseResponse));

        // A stolen token being replayed is treated as a compromised account, not as one bad
        // session, so the sessions that were still valid go down with it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(untouched.RefreshToken)).StatusCode);
        // Reuse detection revokes the session backing the current access token as well.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/sessions")).StatusCode);
    }

    [Fact]
    public async Task RevokeAllSessions_InvalidatesEveryRefreshToken()
    {
        var (email, password) = await CreateUserAsync();
        using var client = _factory.CreateClient();

        var first = await LoginAsync(client, email, password);
        var second = await LoginAsync(client, email, password);

        Authorize(client, second.AccessToken);
        await client.AddReauthenticationProofAsync(password, "session.revoke-all");
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/auth/sessions")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/sessions")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(first.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(second.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task RevokingSomebodyElsesSession_IsRejected()
    {
        var (victimEmail, victimPassword) = await CreateUserAsync();
        var (attackerEmail, attackerPassword) = await CreateUserAsync();

        using var victimClient = _factory.CreateClient();
        var victim = await LoginAsync(victimClient, victimEmail, victimPassword);
        Authorize(victimClient, victim.AccessToken);
        var victimSession = (await ReadDataAsync<List<SessionDto>>(
            await victimClient.GetAsync("/api/auth/sessions"))).Single();

        using var attackerClient = _factory.CreateClient();
        var attacker = await LoginAsync(attackerClient, attackerEmail, attackerPassword);
        Authorize(attackerClient, attacker.AccessToken);

        var response = await attackerClient.DeleteAsync($"/api/auth/sessions/{victimSession.Id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("SESSION_NOT_FOUND", await ReadErrorCodeAsync(response));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(victim.RefreshToken)).StatusCode);
    }

    // --- Trusted devices ---

    [Fact]
    public async Task TrustedDevices_AreListedAndRevokedIndividually()
    {
        var (email, password) = await CreateUserAsync();
        var userId = await GetUserIdAsync(email);
        var keptId = await AddTrustedDeviceAsync(userId, "Laptop");
        var revokedId = await AddTrustedDeviceAsync(userId, "Phone");

        using var client = _factory.CreateClient();
        Authorize(client, (await LoginAsync(client, email, password)).AccessToken);

        var devices = await ReadDataAsync<List<TrustedDeviceDto>>(
            await client.GetAsync("/api/auth/trusted-devices"));
        Assert.Equal(2, devices.Count);

        Assert.Equal(HttpStatusCode.OK,
            (await client.DeleteAsync($"/api/auth/trusted-devices/{revokedId}")).StatusCode);

        var remaining = await ReadDataAsync<List<TrustedDeviceDto>>(
            await client.GetAsync("/api/auth/trusted-devices"));
        Assert.Equal(keptId, Assert.Single(remaining).Id);
    }

    [Fact]
    public async Task ExpiredTrustedDevices_AreNotListed()
    {
        var (email, password) = await CreateUserAsync();
        var userId = await GetUserIdAsync(email);
        await AddTrustedDeviceAsync(userId, "Expired", expiresAt: DateTime.UtcNow.AddDays(-1));
        var activeId = await AddTrustedDeviceAsync(userId, "Active");

        using var client = _factory.CreateClient();
        Authorize(client, (await LoginAsync(client, email, password)).AccessToken);

        var devices = await ReadDataAsync<List<TrustedDeviceDto>>(
            await client.GetAsync("/api/auth/trusted-devices"));

        Assert.Equal(activeId, Assert.Single(devices).Id);
    }

    [Fact]
    public async Task RevokeAllTrustedDevices_RemovesThemAll()
    {
        var (email, password) = await CreateUserAsync();
        var userId = await GetUserIdAsync(email);
        await AddTrustedDeviceAsync(userId, "Laptop");
        await AddTrustedDeviceAsync(userId, "Phone");

        using var client = _factory.CreateClient();
        Authorize(client, (await LoginAsync(client, email, password)).AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/auth/trusted-devices")).StatusCode);

        var devices = await ReadDataAsync<List<TrustedDeviceDto>>(
            await client.GetAsync("/api/auth/trusted-devices"));
        Assert.Empty(devices);
    }

    [Fact]
    public async Task RevokingSomebodyElsesTrustedDevice_IsRejected()
    {
        var (victimEmail, _) = await CreateUserAsync();
        var victimDeviceId = await AddTrustedDeviceAsync(await GetUserIdAsync(victimEmail), "Victim laptop");

        var (attackerEmail, attackerPassword) = await CreateUserAsync();
        using var client = _factory.CreateClient();
        Authorize(client, (await LoginAsync(client, attackerEmail, attackerPassword)).AccessToken);

        var response = await client.DeleteAsync($"/api/auth/trusted-devices/{victimDeviceId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("TRUSTED_DEVICE_NOT_FOUND", await ReadErrorCodeAsync(response));
    }

    // --- External providers ---

    [Fact]
    public async Task ExternalProviders_AreListedAndUnlinked()
    {
        var (email, password) = await CreateUserAsync();
        var userId = await GetUserIdAsync(email);
        var googleId = await AddExternalProviderAsync(userId, "Google", email);
        await AddExternalProviderAsync(userId, "GitHub", email);

        using var client = _factory.CreateClient();
        Authorize(client, (await LoginAsync(client, email, password)).AccessToken);

        var providers = await ReadDataAsync<List<ExternalProviderDto>>(
            await client.GetAsync("/api/auth/external-providers"));
        Assert.Equal(2, providers.Count);
        Assert.Contains(providers, provider => provider.Provider == "Google");

        Assert.Equal(HttpStatusCode.OK,
            (await client.DeleteAsync($"/api/auth/external-providers/{googleId}")).StatusCode);

        var remaining = await ReadDataAsync<List<ExternalProviderDto>>(
            await client.GetAsync("/api/auth/external-providers"));
        Assert.Equal("GitHub", Assert.Single(remaining).Provider);
    }

    [Fact]
    public async Task UnlinkingTheOnlyLoginMethod_IsRejected()
    {
        // A user with no local password whose single provider is unlinked would have no way back in.
        var (email, password) = await CreateUserAsync();
        var userId = await GetUserIdAsync(email);
        var providerId = await AddExternalProviderAsync(userId, "Google", email);

        using var client = _factory.CreateClient();
        Authorize(client, (await LoginAsync(client, email, password)).AccessToken);
        await SetHasLocalPasswordAsync(userId, false);

        var response = await client.DeleteAsync($"/api/auth/external-providers/{providerId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("CANNOT_UNLINK_LAST_PROVIDER", await ReadErrorCodeAsync(response));

        var providers = await ReadDataAsync<List<ExternalProviderDto>>(
            await client.GetAsync("/api/auth/external-providers"));
        Assert.Single(providers);
    }

    [Fact]
    public async Task UnlinkingSomebodyElsesProvider_IsRejected()
    {
        var (victimEmail, _) = await CreateUserAsync();
        var victimUserId = await GetUserIdAsync(victimEmail);
        var victimProviderId = await AddExternalProviderAsync(victimUserId, "Google", victimEmail);

        var (attackerEmail, attackerPassword) = await CreateUserAsync();
        using var client = _factory.CreateClient();
        Authorize(client, (await LoginAsync(client, attackerEmail, attackerPassword)).AccessToken);

        var response = await client.DeleteAsync($"/api/auth/external-providers/{victimProviderId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("PROVIDER_NOT_FOUND", await ReadErrorCodeAsync(response));
    }

    // --- Email change ---

    [Fact]
    public async Task EmailChange_TakesEffectOnlyAfterConfirmation_AndMovesTheLogin()
    {
        var (email, password) = await CreateUserAsync();
        var userId = await GetUserIdAsync(email);
        var newEmail = $"moved-{Guid.NewGuid():N}@example.com";

        using var client = _factory.CreateClient();
        Authorize(client, (await LoginAsync(client, email, password)).AccessToken);
        await client.AddReauthenticationProofAsync(password, "account.change-email");

        var requestResponse = await client.PostAsJsonAsync("/api/auth/email-change/request",
            new RequestEmailChangeRequest { NewEmail = newEmail });
        Assert.Equal(HttpStatusCode.OK, requestResponse.StatusCode);

        // Requesting the change must not move the account on its own.
        Assert.Equal(email, await GetEmailAsync(userId));

        var confirmResponse = await client.PostAsJsonAsync("/api/auth/email-change/confirm",
            new ConfirmEmailChangeRequest
            {
                UserId = userId,
                NewEmail = newEmail,
                Token = await GenerateEmailChangeTokenAsync(userId, newEmail)
            });
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);

        Assert.Equal(newEmail, await GetEmailAsync(userId));
        Assert.Equal(newEmail, await GetUserNameAsync(userId));

        using var freshClient = _factory.CreateClient();
        var afterChange = await LoginAsync(freshClient, newEmail, password);
        Assert.False(string.IsNullOrWhiteSpace(afterChange.AccessToken));

        var oldEmailLogin = await freshClient.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = "AUTHCENTER"
        });
        Assert.NotEqual(HttpStatusCode.OK, oldEmailLogin.StatusCode);
    }

    [Fact]
    public async Task EmailChange_ToAnAddressAlreadyInUse_IsRejected()
    {
        var (email, password) = await CreateUserAsync();
        var (takenEmail, _) = await CreateUserAsync();

        using var client = _factory.CreateClient();
        Authorize(client, (await LoginAsync(client, email, password)).AccessToken);
        await client.AddReauthenticationProofAsync(password, "account.change-email");

        var response = await client.PostAsJsonAsync("/api/auth/email-change/request",
            new RequestEmailChangeRequest { NewEmail = takenEmail });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("EMAIL_TAKEN", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task EmailChange_ToTheCurrentAddress_IsRejected()
    {
        var (email, password) = await CreateUserAsync();

        using var client = _factory.CreateClient();
        Authorize(client, (await LoginAsync(client, email, password)).AccessToken);
        await client.AddReauthenticationProofAsync(password, "account.change-email");

        var response = await client.PostAsJsonAsync("/api/auth/email-change/request",
            new RequestEmailChangeRequest { NewEmail = email });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("SAME_EMAIL", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task ConfirmingAnEmailChange_WithAForgedToken_IsRejected()
    {
        var (email, password) = await CreateUserAsync();
        var userId = await GetUserIdAsync(email);

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/email-change/confirm",
            new ConfirmEmailChangeRequest
            {
                UserId = userId,
                NewEmail = $"forged-{Guid.NewGuid():N}@example.com",
                Token = "not-a-real-token"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("EMAIL_CHANGE_FAILED", await ReadErrorCodeAsync(response));
        Assert.Equal(email, await GetEmailAsync(userId));
    }

    // --- Account deletion ---

    [Fact]
    public async Task DeleteAccount_AnonymizesTheUser_RevokesSessions_AndBlocksLogin()
    {
        var (email, password) = await CreateUserAsync();
        var userId = await GetUserIdAsync(email);

        using var client = _factory.CreateClient();
        var auth = await LoginAsync(client, email, password);
        Authorize(client, auth.AccessToken);

        var response = await SendDeleteAccountAsync(client, new DeleteAccountRequest
        {
            Password = password,
            ConfirmDeletion = true
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var deleted = await GetUserAsync(userId);
        Assert.NotNull(deleted.DeletedAt);
        Assert.False(deleted.IsActive);
        Assert.Equal("Deleted User", deleted.FullName);
        Assert.DoesNotContain(email, deleted.Email);
        Assert.EndsWith("@deleted.invalid", deleted.Email);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(auth.RefreshToken)).StatusCode);

        using var freshClient = _factory.CreateClient();
        var loginAfterDeletion = await freshClient.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = "AUTHCENTER"
        });
        Assert.NotEqual(HttpStatusCode.OK, loginAfterDeletion.StatusCode);
    }

    [Fact]
    public async Task DeleteAccount_WithoutExplicitConfirmation_IsRejected()
    {
        var (email, password) = await CreateUserAsync();

        using var client = _factory.CreateClient();
        Authorize(client, (await LoginAsync(client, email, password)).AccessToken);

        var response = await SendDeleteAccountAsync(client, new DeleteAccountRequest
        {
            Password = password,
            ConfirmDeletion = false
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("DELETION_NOT_CONFIRMED", await ReadErrorCodeAsync(response));
        Assert.Null((await GetUserAsync(await GetUserIdAsync(email))).DeletedAt);
    }

    [Fact]
    public async Task DeleteAccount_WithTheWrongPassword_IsRejected()
    {
        var (email, password) = await CreateUserAsync();

        using var client = _factory.CreateClient();
        Authorize(client, (await LoginAsync(client, email, password)).AccessToken);

        var response = await SendDeleteAccountAsync(client, new DeleteAccountRequest
        {
            Password = "Wrong_Password_123",
            ConfirmDeletion = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_PASSWORD", await ReadErrorCodeAsync(response));
        Assert.Null((await GetUserAsync(await GetUserIdAsync(email))).DeletedAt);
    }

    // --- Helpers ---

    private static void Authorize(HttpClient client, string accessToken)
        => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    private static async Task<AuthResponse> LoginAsync(HttpClient client, string email, string password)
    {
        var previousAuthorization = client.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Authorization = null;

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = "AUTHCENTER"
        });

        client.DefaultRequestHeaders.Authorization = previousAuthorization;
        response.EnsureSuccessStatusCode();
        return await ReadDataAsync<AuthResponse>(response);
    }

    private async Task<HttpResponseMessage> RefreshAsync(string refreshToken)
    {
        using var client = _factory.CreateClient();
        return await client.PostAsJsonAsync("/api/auth/refresh-token", new RefreshTokenRequest
        {
            RefreshToken = refreshToken,
            ApplicationCode = "AUTHCENTER"
        });
    }

    /// <summary>DELETE with a body, which HttpClient has no shorthand for.</summary>
    private static async Task<HttpResponseMessage> SendDeleteAccountAsync(HttpClient client, DeleteAccountRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(request)
        };
        return await client.SendAsync(message);
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        Assert.NotNull(body);
        Assert.True(body.Success, $"Expected success but got: {body.ErrorCode} — {body.Message}");
        Assert.NotNull(body.Data);
        return body.Data;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        return body?.ErrorCode;
    }

    private async Task<(string Email, string Password)> CreateUserAsync()
    {
        const string password = "Account12345";
        var email = $"account-{Guid.NewGuid():N}@example.com";

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var application = await db.ApplicationSystems.SingleAsync(a => a.Code == "AUTHCENTER");
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = "Account Owner",
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            HasLocalPassword = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

        db.UserApplicationAccesses.Add(new UserApplicationAccess
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ApplicationSystemId = application.Id,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return (email, password);
    }

    private async Task<Guid> AddTrustedDeviceAsync(Guid userId, string deviceName, DateTime? expiresAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();

        var device = new UserTrustedDevice
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Guid.NewGuid().ToString("N"),
            DeviceName = deviceName,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(30)
        };

        db.UserTrustedDevices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<Guid> AddExternalProviderAsync(Guid userId, string provider, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();

        var link = new ExternalIdentityProvider
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = provider,
            ProviderUserId = Guid.NewGuid().ToString("N"),
            Email = email,
            DisplayName = "Account Owner",
            LinkedAt = DateTime.UtcNow,
            IsActive = true
        };

        db.ExternalIdentityProviders.Add(link);
        await db.SaveChangesAsync();
        return link.Id;
    }

    private async Task SetHasLocalPasswordAsync(Guid userId, bool hasLocalPassword)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();

        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.HasLocalPassword = hasLocalPassword;
        await db.SaveChangesAsync();
    }

    private async Task<string> GenerateEmailChangeTokenAsync(Guid userId, string newEmail)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await userManager.FindByIdAsync(userId.ToString());
        return await userManager.GenerateChangeEmailTokenAsync(user!, newEmail);
    }

    private async Task<ApplicationUser> GetUserAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    /// <summary>
    /// Maps a refresh token back to the session row it belongs to, so a test can revoke exactly
    /// the session it means to instead of guessing from timestamps.
    /// </summary>
    private async Task<Guid> GetSessionIdAsync(string refreshToken)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();

        var hash = tokenService.HashToken(refreshToken);
        return (await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenHash == hash)).Id;
    }

    private async Task<Guid> GetUserIdAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Email == email)).Id;
    }

    private async Task<string> GetEmailAsync(Guid userId) => (await GetUserAsync(userId)).Email!;

    private async Task<string> GetUserNameAsync(Guid userId) => (await GetUserAsync(userId)).UserName!;
}
