using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Applications;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Policies;
using AuthCenter.Contracts.Requests.Users;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Applications;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Policies;
using AuthCenter.Contracts.Responses.Users;
using AuthCenter.Domain.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

public class AccessPolicyTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public AccessPolicyTests(AuthCenterWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OrderedPolicies_AllowThenDenyLogin_AndRevokeExistingSession()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var (user, password) = await CreateUserAsync(admin, application.Id);

        var allow = await ReadDataAsync<AccessPolicyRuleDto>(await admin.PostAsJsonAsync("/api/access-policies",
            new CreateAccessPolicyRuleRequest
            {
                ApplicationSystemId = application.Id,
                Name = "Local integration traffic",
                Priority = 100,
                Action = "Allow"
            }));
        Assert.Empty(allow.IncludedIpCidrs);

        using var loginClient = _factory.CreateClient();
        var firstLogin = await ReadDataAsync<AuthResponse>(await LoginAsync(loginClient, user.Email, password, application.Code));

        var deny = await ReadDataAsync<AccessPolicyRuleDto>(await admin.PostAsJsonAsync("/api/access-policies",
            new CreateAccessPolicyRuleRequest
            {
                ApplicationSystemId = application.Id,
                Name = "Emergency deny",
                Priority = 1,
                Action = "Deny"
            }));

        var denied = await LoginAsync(loginClient, user.Email, password, application.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var deniedBody = await denied.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("ACCESS_POLICY_DENIED", deniedBody?.ErrorCode);

        var revoked = await loginClient.PostAsJsonAsync("/api/auth/refresh-token", new RefreshTokenRequest
        {
            RefreshToken = firstLogin.RefreshToken,
            ApplicationCode = application.Code
        });
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/access-policies/{deny.Id}")).StatusCode);
        Assert.True((await LoginAsync(loginClient, user.Email, password, application.Code)).IsSuccessStatusCode);

        var rules = await ReadDataAsync<List<AccessPolicyRuleDto>>(
            await admin.GetAsync($"/api/access-policies/applications/{application.Id}"));
        Assert.Single(rules);
        Assert.Equal(allow.Id, rules[0].Id);
    }

    [Fact]
    public async Task PolicyEvaluation_IsFailClosed_AndSupportsGroupMfaAndIpExclusions()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var (user, _) = await CreateUserAsync(admin, application.Id);

        var invalid = await admin.PostAsJsonAsync("/api/access-policies", new CreateAccessPolicyRuleRequest
        {
            ApplicationSystemId = application.Id,
            Name = "Invalid network",
            Priority = 1,
            IncludedIpCidrs = ["not-a-cidr"]
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var groupResponse = await admin.PostAsJsonAsync("/api/groups", new
        {
            Name = $"Policy group {Guid.NewGuid():N}"
        });
        var group = await ReadDataAsync<AuthCenter.Contracts.Responses.Groups.DirectoryGroupDto>(groupResponse);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsync($"/api/groups/{group.Id}/members/{user.Id}", null)).StatusCode);

        await ReadDataAsync<AccessPolicyRuleDto>(await admin.PostAsJsonAsync("/api/access-policies",
            new CreateAccessPolicyRuleRequest
            {
                ApplicationSystemId = application.Id,
                DirectoryGroupId = group.Id,
                Name = "Group MFA outside blocked network",
                Priority = 10,
                MfaRequirement = "Required",
                AllowTrustedDeviceBypass = false,
                IncludedIpCidrs = ["10.0.0.0/8"],
                ExcludedIpCidrs = ["10.10.0.0/16"]
            }));

        await using var scope = _factory.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IAccessPolicyService>();
        var allowed = await policies.EvaluateAsync(user.Id, application.Id, "10.20.1.2");
        Assert.True(allowed.IsAllowed);
        Assert.True(allowed.RequireMfa);
        Assert.False(allowed.AllowTrustedDeviceBypass);

        Assert.False((await policies.EvaluateAsync(user.Id, application.Id, "10.10.1.2")).IsAllowed);
        Assert.False((await policies.EvaluateAsync(user.Id, application.Id, "192.168.1.2")).IsAllowed);
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateClient();
        var auth = await ReadDataAsync<AuthResponse>(await LoginAsync(
            client,
            AuthCenterWebApplicationFactory.AdminEmail,
            AuthCenterWebApplicationFactory.AdminPassword,
            DomainConstants.SystemCodes.AuthCenter));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private static async Task<ApplicationDto> CreateApplicationAsync(HttpClient admin)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        return await ReadDataAsync<ApplicationDto>(await admin.PostAsJsonAsync("/api/applications",
            new CreateApplicationRequest
            {
                Code = $"POL{suffix}",
                Name = $"Policy test {suffix}",
                RegistrationMode = "InviteOnly",
                AllowPasswordLogin = true
            }));
    }

    private static async Task<(UserDto User, string Password)> CreateUserAsync(HttpClient admin, Guid applicationId)
    {
        var password = TestSecretGenerator.CreatePassword();
        var user = await ReadDataAsync<UserDto>(await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            FullName = "Policy User",
            Email = $"policy-{Guid.NewGuid():N}@example.com",
            Password = password,
            ApplicationSystemId = applicationId,
            GrantApplicationAccess = true
        }));
        return (user, password);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password, string applicationCode) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = applicationCode
        });

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        Assert.NotNull(body);
        Assert.True(body.Success, $"Expected success but got {body.ErrorCode}: {body.Message}");
        Assert.NotNull(body.Data);
        return body.Data;
    }
}
