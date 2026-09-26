using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Policies;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// A single sign-on session is only reused under the rules of the client's own application: its
/// access policy, its MFA requirement, the user's MFA and the acr_values the client asks for. A
/// weaker session is stepped up in place; a denial goes back to the client; refresh re-checks.
/// </summary>
public sealed class SingleSignOnPolicyTests : IClassFixture<HttpsAuthCenterFactory>
{
    private const string Redirect = "https://policy-rp.test/callback";
    private readonly HttpsAuthCenterFactory _factory;

    public SingleSignOnPolicyTests(HttpsAuthCenterFactory factory) => _factory = factory;

    [Fact]
    public async Task PasswordSession_StepsUpWithTheSecondFactor_AndKeepsTheSession()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin, requireMfa: true);
        var (email, password) = await CreateUserAsync(DomainConstants.SystemCodes.AuthCenter, application.Code);
        var authCenterClient = await RegisterClientAsync(admin, DomainConstants.SystemCodes.AuthCenter);
        var targetClient = await RegisterClientAsync(admin, application.Code);
        using var browser = CreateBrowser();
        var csrf = await SignInAsync(browser, email, password);
        var before = await AuthorizeAndExchangeAsync(browser, authCenterClient);
        var secret = await EnableTotpAsync(email, password);

        Assert.Equal("login_required", await SilentErrorAsync(browser, targetClient));
        var (url, verifier) = AuthorizeUrl(targetClient);
        var interactionId = InteractionId(await browser.GetAsync(url));
        var stale = await CompleteAsync(browser, csrf, interactionId);
        Assert.Equal("STEP_UP_REQUIRED", (await stale.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode);

        var stepUp = await ReadDataAsync(await PostAsync(browser, csrf, $"/oauth/interactions/{interactionId}/step-up", null));
        Assert.True(stepUp.GetProperty("requiresMfa").GetBoolean());
        var verified = await PostAsync(browser, csrf, "/ui-api/session/mfa", new VerifyMfaRequest
        {
            MfaPendingToken = stepUp.GetProperty("mfaPendingToken").GetString()!,
            TotpCode = TestTotp.Code(secret)
        });
        csrf = (await ReadDataAsync(verified)).GetProperty("csrfToken").GetString()!;
        var complete = await CompleteAsync(browser, csrf, interactionId);
        var redirectUrl = (await ReadDataAsync(complete)).GetProperty("redirectUrl").GetString()!;
        var after = await ExchangeAsync(targetClient, QueryHelpers.ParseQuery(new Uri(redirectUrl).Query)["code"].ToString(), verifier);

        var idToken = new JwtSecurityTokenHandler().ReadJwtToken(after.GetProperty("id_token").GetString());
        Assert.Equal(DomainConstants.AuthenticationContextClasses.MultiFactor, Claim(idToken, "acr"));
        Assert.Equal(
            [DomainConstants.AuthenticationMethods.Password, DomainConstants.AuthenticationMethods.OneTimePassword, DomainConstants.AuthenticationMethods.MultiFactor],
            idToken.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value).ToArray());
        Assert.Equal(Claim(before, "sid"), Claim(idToken, "sid"));

        // The stepped-up session now satisfies the application silently.
        Assert.Null(await SilentErrorAsync(browser, targetClient));
    }

    [Fact]
    public async Task ApplicationRequiringMfa_ForAUserWithoutMfa_EnrollsInPlaceAndCompletesTheRequest()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin, requireMfa: true);
        var (email, password) = await CreateUserAsync(DomainConstants.SystemCodes.AuthCenter, application.Code);
        var targetClient = await RegisterClientAsync(admin, application.Code);
        using var browser = CreateBrowser();
        var csrf = await SignInAsync(browser, email, password);

        var (url, verifier) = AuthorizeUrl(targetClient);
        var interactionId = InteractionId(await browser.GetAsync(url));
        var stepUp = await ReadDataAsync(await PostAsync(browser, csrf, $"/oauth/interactions/{interactionId}/step-up", null));

        // No code is issued: the hosted login enrolls the authenticator with a single-use token.
        Assert.True(stepUp.GetProperty("requiresMfaEnrollment").GetBoolean());
        Assert.False(stepUp.TryGetProperty("mfaPendingToken", out _));
        var enrollmentToken = stepUp.GetProperty("enrollmentToken").GetString()!;
        var setup = await ReadDataAsync(await PostAsync(browser, csrf, "/ui-api/session/mfa/enrollment/start", new TotpEnrollmentRequest { EnrollmentToken = enrollmentToken }));
        var secret = setup.GetProperty("secretBase32").GetString()!;
        var wrong = await PostAsync(browser, csrf, "/ui-api/session/mfa/enrollment/complete", new TotpEnrollmentRequest { EnrollmentToken = enrollmentToken, TotpCode = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var enrolled = await ReadDataAsync(await PostAsync(browser, csrf, "/ui-api/session/mfa/enrollment/complete", new TotpEnrollmentRequest
        {
            EnrollmentToken = enrollmentToken,
            TotpCode = TestTotp.Code(secret)
        }));
        Assert.Equal(8, enrolled.GetProperty("backupCodes").GetArrayLength());
        csrf = enrolled.GetProperty("csrfToken").GetString()!;

        var complete = await CompleteAsync(browser, csrf, interactionId);
        var redirectUrl = (await ReadDataAsync(complete)).GetProperty("redirectUrl").GetString()!;
        var tokens = await ExchangeAsync(targetClient, QueryHelpers.ParseQuery(new Uri(redirectUrl).Query)["code"].ToString(), verifier);
        var idToken = new JwtSecurityTokenHandler().ReadJwtToken(tokens.GetProperty("id_token").GetString());
        Assert.Equal(DomainConstants.AuthenticationContextClasses.MultiFactor, Claim(idToken, "acr"));
        // The enrollment token was spent with the enrollment.
        var replay = await PostAsync(browser, csrf, "/ui-api/session/mfa/enrollment/start", new TotpEnrollmentRequest { EnrollmentToken = enrollmentToken });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task AcrValuesForMfa_AreNotSatisfiedByAPasswordSession()
    {
        using var admin = await CreateAdminClientAsync();
        var (email, password) = await CreateUserAsync(DomainConstants.SystemCodes.AuthCenter);
        var client = await RegisterClientAsync(admin, DomainConstants.SystemCodes.AuthCenter);
        using var browser = CreateBrowser();
        await SignInAsync(browser, email, password);

        Assert.Null(await SilentErrorAsync(browser, client));
        Assert.Equal("login_required", await SilentErrorAsync(browser, client, new() { ["acr_values"] = DomainConstants.AuthenticationContextClasses.MultiFactor }));
        // Acceptable classes are listed by preference: the least demanding supported one applies.
        Assert.Null(await SilentErrorAsync(browser, client, new()
        {
            ["acr_values"] = $"{DomainConstants.AuthenticationContextClasses.MultiFactor} {DomainConstants.AuthenticationContextClasses.SingleFactor}"
        }));
    }

    [Fact]
    public async Task ApplicationPolicyDenial_IsAnsweredToTheClientWithoutALoginPage()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin, requireMfa: false);
        var (email, password) = await CreateUserAsync(DomainConstants.SystemCodes.AuthCenter, application.Code);
        var targetClient = await RegisterClientAsync(admin, application.Code);
        var userId = await UserIdAsync(email);
        await PublishPolicyAsync(admin, application.Id, new CreateAccessPolicyRuleRequest
        {
            ApplicationSystemId = application.Id,
            UserId = userId,
            Name = "Deny this user",
            Priority = 10,
            Action = "Deny"
        });
        using var browser = CreateBrowser();
        await SignInAsync(browser, email, password);

        var response = await browser.GetAsync(AuthorizeUrl(targetClient).Url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(Redirect, response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal("access_denied", QueryHelpers.ParseQuery(response.Headers.Location.Query)["error"].ToString());
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(log => log.Action == "ACCESS_POLICY_DENIED" && log.UserId == userId && log.ApplicationCode == application.Code));
    }

    [Fact]
    public async Task Refresh_AfterTheUserLeavesTheAllowedGroup_IsRejectedAndTheGrantRevoked()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin, requireMfa: false);
        var (email, password) = await CreateUserAsync(DomainConstants.SystemCodes.AuthCenter, application.Code);
        var userId = await UserIdAsync(email);
        var groupId = await CreateGroupWithMemberAsync(userId);
        await PublishPolicyAsync(admin, application.Id, new CreateAccessPolicyRuleRequest
        {
            ApplicationSystemId = application.Id,
            DirectoryGroupId = groupId,
            Name = "Allow the project group",
            Priority = 10,
            Action = "Allow"
        });
        var targetClient = await RegisterClientAsync(admin, application.Code, offlineAccess: true);
        using var browser = CreateBrowser();
        await SignInAsync(browser, email, password);
        var (url, verifier) = AuthorizeUrl(targetClient, scope: "openid offline_access");
        var authorize = await browser.GetAsync(url);
        var tokens = await ExchangeAsync(targetClient, QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString(), verifier);
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var stillAllowed = await RefreshAsync(targetClient, refreshToken);
        Assert.Equal(HttpStatusCode.OK, stillAllowed.StatusCode);
        refreshToken = (await stillAllowed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refresh_token").GetString()!;

        await RemoveMembershipAsync(groupId, userId);
        var denied = await RefreshAsync(targetClient, refreshToken);

        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal("invalid_grant", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        Assert.DoesNotContain(await db.RefreshTokens.AsNoTracking()
            .Where(token => token.UserId == userId && token.OAuthClientId == targetClient)
            .Select(token => token.RevokedAt)
            .ToListAsync(), revokedAt => revokedAt is null);
    }

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(HttpsAuthCenterFactory.Authority),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<string> SignInAsync(HttpClient browser, string email, string password)
    {
        var login = await browser.PostAsJsonAsync("/ui-api/session/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        return (await ReadDataAsync(login)).GetProperty("csrfToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string csrf, string path, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body, body.GetType()) };
        request.Headers.Add("X-AuthCenter-CSRF", csrf);
        return await browser.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> CompleteAsync(HttpClient browser, string csrf, string interactionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/authorize/complete")
        {
            Content = JsonContent.Create(new { InteractionId = interactionId, Consent = true })
        };
        request.Headers.Add("X-AuthCenter-CSRF", csrf);
        request.Headers.Add("X-AuthCenter-UI", "1");
        return await browser.SendAsync(request);
    }

    /// <summary>The OAuth error of a prompt=none request, or null when a code was issued.</summary>
    private static async Task<string?> SilentErrorAsync(HttpClient browser, string clientId, Dictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string>(extra ?? []) { ["prompt"] = "none" };
        var response = await browser.GetAsync(AuthorizeUrl(clientId, parameters).Url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        if (query.TryGetValue("error", out var error))
            return error.ToString();
        Assert.False(string.IsNullOrEmpty(query["code"].ToString()));
        return null;
    }

    private async Task<JwtSecurityToken> AuthorizeAndExchangeAsync(HttpClient browser, string clientId)
    {
        var (url, verifier) = AuthorizeUrl(clientId);
        var response = await browser.GetAsync(url);
        var code = QueryHelpers.ParseQuery(response.Headers.Location!.Query)["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code), response.Headers.Location.OriginalString);
        var tokens = await ExchangeAsync(clientId, code, verifier);
        return new JwtSecurityTokenHandler().ReadJwtToken(tokens.GetProperty("id_token").GetString());
    }

    private async Task<JsonElement> ExchangeAsync(string clientId, string code, string verifier)
    {
        using var backChannel = _factory.CreateAuthCenterClient();
        var token = await backChannel.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = Redirect,
            ["code_verifier"] = verifier
        }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> RefreshAsync(string clientId, string refreshToken)
    {
        using var backChannel = _factory.CreateAuthCenterClient();
        return await backChannel.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken
        }));
    }

    private static (string Url, string Verifier) AuthorizeUrl(string clientId, Dictionary<string, string>? extra = null, string scope = "openid profile email")
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = Redirect,
            ["scope"] = scope,
            ["state"] = "state-value",
            ["nonce"] = "nonce-value",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256"
        };
        foreach (var (key, value) in extra ?? [])
            parameters[key] = value;
        return (QueryHelpers.AddQueryString("/oauth/authorize", parameters), verifier);
    }

    private static string InteractionId(HttpResponseMessage authorize)
    {
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var interactionId = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["interaction_id"].ToString();
        Assert.False(string.IsNullOrEmpty(interactionId), authorize.Headers.Location!.OriginalString);
        return interactionId;
    }

    private async Task<string> RegisterClientAsync(HttpClient admin, string applicationCode, bool offlineAccess = false)
    {
        var clientId = $"pol-{Guid.NewGuid():N}"[..24];
        var response = await admin.PostAsJsonAsync("/api/oauth/clients", new
        {
            ApplicationSystemId = await ApplicationIdAsync(applicationCode),
            ClientId = clientId,
            DisplayName = "Policy test client",
            ClientType = 1,
            RedirectUris = new[] { Redirect },
            AllowedScopes = offlineAccess ? new[] { "openid", "profile", "email", "offline_access" } : new[] { "openid", "profile", "email" },
            GrantTypes = offlineAccess ? new[] { "authorization_code", "refresh_token" } : new[] { "authorization_code" },
            LoginUrl = $"{HttpsAuthCenterFactory.Authority}/login",
            RequirePkce = true,
            AutoConsent = true
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return clientId;
    }

    private static async Task<(Guid Id, string Code)> CreateApplicationAsync(HttpClient admin, bool requireMfa)
    {
        var code = "SSOPOL" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await ReadDataAsync(await admin.PostAsJsonAsync("/api/applications", new
        {
            Code = code,
            Name = "SSO policy " + code,
            RegistrationMode = "Closed",
            RequireMfa = requireMfa
        }));
        return (created.GetProperty("id").GetGuid(), code);
    }

    private static async Task PublishPolicyAsync(HttpClient admin, Guid applicationId, CreateAccessPolicyRuleRequest rule)
    {
        var draft = await ReadDataAsync(await admin.PostAsync($"/api/access-policies/applications/{applicationId}/drafts", null));
        var draftId = draft.GetProperty("id").GetGuid();
        await ReadDataAsync(await admin.PostAsJsonAsync("/api/access-policies", new CreateAccessPolicyRuleRequest
        {
            ApplicationSystemId = rule.ApplicationSystemId,
            PolicyVersionId = draftId,
            UserId = rule.UserId,
            DirectoryGroupId = rule.DirectoryGroupId,
            Name = rule.Name,
            Priority = rule.Priority,
            Action = rule.Action,
            MfaRequirement = rule.MfaRequirement
        }));
        await admin.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.access-policy.publish");
        await ReadDataAsync(await admin.PostAsync($"/api/access-policies/applications/{applicationId}/versions/{draftId}/publish", null));
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateAuthCenterClient();
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

    private async Task<string> EnableTotpAsync(string email, string password)
    {
        using var client = _factory.CreateAuthCenterClient();
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

    private async Task<(string Email, string Password)> CreateUserAsync(params string[] applicationCodes)
    {
        var email = $"sso-policy-{Guid.NewGuid():N}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = "Policy SSO User",
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            HasLocalPassword = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
        foreach (var code in applicationCodes)
        {
            var application = await db.ApplicationSystems.SingleAsync(item => item.Code == code);
            db.UserApplicationAccesses.Add(new UserApplicationAccess
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ApplicationSystemId = application.Id,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
        return (email, password);
    }

    private async Task<Guid> CreateGroupWithMemberAsync(Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var name = $"Project {Guid.NewGuid():N}";
        var group = new DirectoryGroup { Id = Guid.NewGuid(), Name = name, NormalizedName = name.ToUpperInvariant(), IsActive = true, CreatedAt = DateTime.UtcNow };
        db.DirectoryGroups.Add(group);
        db.UserGroupMemberships.Add(new UserGroupMembership { GroupId = group.Id, UserId = userId, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return group.Id;
    }

    private async Task RemoveMembershipAsync(Guid groupId, Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        db.UserGroupMemberships.Remove(await db.UserGroupMemberships.SingleAsync(item => item.GroupId == groupId && item.UserId == userId));
        await db.SaveChangesAsync();
    }

    private async Task<Guid> UserIdAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
    }

    private async Task<Guid> ApplicationIdAsync(string code)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.ApplicationSystems.Where(application => application.Code == code).Select(application => application.Id).SingleAsync();
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("data").Clone();
    }

    private static string Claim(JwtSecurityToken token, string type) => token.Claims.Single(claim => claim.Type == type).Value;

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
