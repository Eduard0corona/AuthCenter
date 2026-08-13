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
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    public async Task Drafts_DoNotAffectRuntime_PublishingRevokesSessions_AndPublishedVersionsAreImmutable()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var (user, password) = await CreateUserAsync(admin, application.Id);
        var firstDraft = await CreateDraftAsync(admin, application.Id);

        var allow = await CreateRuleAsync(admin, new CreateAccessPolicyRuleRequest
        {
            ApplicationSystemId = application.Id,
            PolicyVersionId = firstDraft.Id,
            Name = "Allow all",
            Priority = 100,
            Action = "Allow"
        });

        using var loginClient = _factory.CreateClient();
        var beforeFirstPublish = await ReadDataAsync<AuthResponse>(
            await LoginAsync(loginClient, user.Email, password, application.Code));
        var publishWithoutProof = await admin.PostAsync(
            $"/api/access-policies/applications/{application.Id}/versions/{firstDraft.Id}/publish",
            null);
        Assert.Equal(HttpStatusCode.Forbidden, publishWithoutProof.StatusCode);
        Assert.Equal("REAUTHENTICATION_REQUIRED", (await publishWithoutProof.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);
        await PublishAsync(admin, application.Id, firstDraft.Id);

        var revokedByFirstPublish = await loginClient.PostAsJsonAsync("/api/auth/refresh-token", new RefreshTokenRequest
        {
            RefreshToken = beforeFirstPublish.RefreshToken,
            ApplicationCode = application.Code
        });
        Assert.Equal(HttpStatusCode.Unauthorized, revokedByFirstPublish.StatusCode);

        var activeSession = await ReadDataAsync<AuthResponse>(
            await LoginAsync(loginClient, user.Email, password, application.Code));
        var secondDraft = await CreateDraftAsync(admin, application.Id);
        Assert.Equal(1, secondDraft.RuleCount);

        var deny = await CreateRuleAsync(admin, new CreateAccessPolicyRuleRequest
        {
            ApplicationSystemId = application.Id,
            PolicyVersionId = secondDraft.Id,
            Name = "Emergency deny",
            Priority = 1,
            Action = "Deny"
        });

        // Editing a draft never changes the published runtime policy or active sessions.
        Assert.True((await LoginAsync(loginClient, user.Email, password, application.Code)).IsSuccessStatusCode);
        var refreshBeforePublish = await loginClient.PostAsJsonAsync("/api/auth/refresh-token", new RefreshTokenRequest
        {
            RefreshToken = activeSession.RefreshToken,
            ApplicationCode = application.Code
        });
        Assert.True(refreshBeforePublish.IsSuccessStatusCode);

        await PublishAsync(admin, application.Id, secondDraft.Id);
        var denied = await LoginAsync(loginClient, user.Email, password, application.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("ACCESS_POLICY_DENIED", (await denied.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);

        var immutable = await admin.DeleteAsync($"/api/access-policies/{deny.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, immutable.StatusCode);
        Assert.Equal(
            "POLICY_VERSION_IMMUTABLE",
            (await immutable.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);

        var versions = await ReadDataAsync<List<AccessPolicyVersionDto>>(
            await admin.GetAsync($"/api/access-policies/applications/{application.Id}/versions"));
        Assert.Equal(2, versions.Count);
        Assert.Equal("Published", versions[0].Status);
        Assert.Equal("Archived", versions[1].Status);

        var publishedRules = await ReadDataAsync<List<AccessPolicyRuleDto>>(
            await admin.GetAsync($"/api/access-policies/applications/{application.Id}?policyVersionId={secondDraft.Id}"));
        Assert.Equal(["Emergency deny", "Allow all"], publishedRules.Select(rule => rule.Name));
        Assert.Equal([1, 100], publishedRules.Select(rule => rule.Priority));

        await using var auditScope = _factory.Services.CreateAsyncScope();
        var auditDb = auditScope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var actions = await auditDb.AuditLogs
            .Where(audit => audit.ApplicationCode == application.Code || audit.EntityId == firstDraft.Id.ToString())
            .Select(audit => audit.Action)
            .ToListAsync();
        Assert.Contains("ACCESS_POLICY_PUBLISH_REJECTED", actions);
        Assert.Contains("ACCESS_POLICY_VERSION_PUBLISHED", actions);
    }

    [Fact]
    public async Task PublishedPolicy_IsFailClosed_AndSupportsUserGroupMfaAndIpExclusions()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var (user, _) = await CreateUserAsync(admin, application.Id);
        var draft = await CreateDraftAsync(admin, application.Id);

        var invalid = await admin.PostAsJsonAsync("/api/access-policies", new CreateAccessPolicyRuleRequest
        {
            ApplicationSystemId = application.Id,
            PolicyVersionId = draft.Id,
            Name = "Invalid network",
            Priority = 1,
            IncludedIpCidrs = ["not-a-cidr"]
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var group = await ReadDataAsync<AuthCenter.Contracts.Responses.Groups.DirectoryGroupDto>(
            await admin.PostAsJsonAsync("/api/groups", new { Name = $"Policy group {Guid.NewGuid():N}" }));
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsync($"/api/groups/{group.Id}/members/{user.Id}", null)).StatusCode);

        await CreateRuleAsync(admin, new CreateAccessPolicyRuleRequest
        {
            ApplicationSystemId = application.Id,
            PolicyVersionId = draft.Id,
            UserId = user.Id,
            DirectoryGroupId = group.Id,
            Name = "User and group MFA outside blocked network",
            Priority = 10,
            MfaRequirement = "Required",
            AllowTrustedDeviceBypass = false,
            IncludedIpCidrs = ["10.0.0.0/8"],
            ExcludedIpCidrs = ["10.10.0.0/16"]
        });
        await PublishAsync(admin, application.Id, draft.Id);

        await using var scope = _factory.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IAccessPolicyService>();
        var allowed = await policies.EvaluateAsync(user.Id, application.Id, "10.20.1.2");
        Assert.True(allowed.IsAllowed);
        Assert.True(allowed.RequireMfa);
        Assert.False(allowed.AllowTrustedDeviceBypass);

        Assert.False((await policies.EvaluateAsync(user.Id, application.Id, "10.10.1.2")).IsAllowed);
        Assert.False((await policies.EvaluateAsync(user.Id, application.Id, "192.168.1.2")).IsAllowed);
    }

    [Fact]
    public async Task Simulation_ExplainsScheduleRiskAndAssurance_BeforePublishing()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var (user, _) = await CreateUserAsync(admin, application.Id);
        var draft = await CreateDraftAsync(admin, application.Id);

        await CreateRuleAsync(admin, new CreateAccessPolicyRuleRequest
        {
            ApplicationSystemId = application.Id,
            PolicyVersionId = draft.Id,
            Name = "Deny high risk during weekday business hours",
            Priority = 10,
            Action = "Deny",
            ActiveDaysUtc = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
            DailyStartTimeUtc = new TimeOnly(9, 0),
            DailyEndTimeUtc = new TimeOnly(17, 0),
            MinimumRiskLevel = "High"
        });
        await CreateRuleAsync(admin, new CreateAccessPolicyRuleRequest
        {
            ApplicationSystemId = application.Id,
            PolicyVersionId = draft.Id,
            Name = "Allow with MFA",
            Priority = 100,
            Action = "Allow",
            RequiredAssuranceLevel = "Mfa",
            AllowTrustedDeviceBypass = false
        });

        var highRisk = await SimulateAsync(admin, new SimulateAccessPolicyRequest
        {
            ApplicationSystemId = application.Id,
            PolicyVersionId = draft.Id,
            UserId = user.Id,
            IpAddress = "203.0.113.10",
            EvaluatedAtUtc = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc), // Monday
            RiskLevel = "High",
            AssuranceLevel = "Password"
        });
        Assert.False(highRisk.IsAllowed);
        Assert.Equal("Deny high risk during weekday business hours", highRisk.MatchedRuleName);
        Assert.True(highRisk.RuleEvaluations[0].Matched);

        var lowRisk = await SimulateAsync(admin, new SimulateAccessPolicyRequest
        {
            ApplicationSystemId = application.Id,
            PolicyVersionId = draft.Id,
            UserId = user.Id,
            EvaluatedAtUtc = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc),
            RiskLevel = "Low",
            AssuranceLevel = "Password"
        });
        Assert.True(lowRisk.IsAllowed);
        Assert.True(lowRisk.RequireMfa);
        Assert.Equal("Mfa", lowRisk.RequiredAssuranceLevel);
        Assert.False(lowRisk.RuleEvaluations[0].Matched);
        Assert.Contains(lowRisk.RuleEvaluations[0].Reasons, reason => reason.Contains("below the rule minimum", StringComparison.Ordinal));
        Assert.True(lowRisk.RuleEvaluations[1].Matched);

        var weekend = await SimulateAsync(admin, new SimulateAccessPolicyRequest
        {
            ApplicationSystemId = application.Id,
            PolicyVersionId = draft.Id,
            UserId = user.Id,
            EvaluatedAtUtc = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc), // Sunday
            RiskLevel = "Critical",
            AssuranceLevel = "Mfa"
        });
        Assert.True(weekend.IsAllowed);
        Assert.False(weekend.RequireMfa);
        Assert.Contains(weekend.RuleEvaluations[0].Reasons, reason => reason.Contains("UTC day", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthCenter_DraftCannotPublishWithoutUnconditionalAllowFallback()
    {
        using var admin = await CreateAdminClientAsync();
        Guid authCenterId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            authCenterId = await db.ApplicationSystems
                .Where(application => application.Code == DomainConstants.SystemCodes.AuthCenter)
                .Select(application => application.Id)
                .SingleAsync();
        }

        var draft = await CreateDraftAsync(admin, authCenterId);
        var deny = await CreateRuleAsync(admin, new CreateAccessPolicyRuleRequest
        {
            ApplicationSystemId = authCenterId,
            PolicyVersionId = draft.Id,
            Name = "Unsafe control-plane deny",
            Priority = 1,
            Action = "Deny"
        });

        await admin.AddReauthenticationProofAsync(
            AuthCenterWebApplicationFactory.AdminPassword,
            "admin.access-policy.publish");
        var publish = await admin.PostAsync(
            $"/api/access-policies/applications/{authCenterId}/versions/{draft.Id}/publish",
            null);
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
        Assert.Equal(
            "AUTHCENTER_POLICY_FALLBACK_REQUIRED",
            (await publish.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/access-policies/{deny.Id}")).StatusCode);
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

    private static Task<AccessPolicyVersionDto> CreateDraftAsync(HttpClient admin, Guid applicationId) =>
        ReadDataAsync<AccessPolicyVersionDto>(admin.PostAsync($"/api/access-policies/applications/{applicationId}/drafts", null));

    private static async Task<AccessPolicyVersionDto> PublishAsync(HttpClient admin, Guid applicationId, Guid versionId)
    {
        await admin.AddReauthenticationProofAsync(
            AuthCenterWebApplicationFactory.AdminPassword,
            "admin.access-policy.publish");
        return await ReadDataAsync<AccessPolicyVersionDto>(
            admin.PostAsync($"/api/access-policies/applications/{applicationId}/versions/{versionId}/publish", null));
    }

    private static Task<AccessPolicyRuleDto> CreateRuleAsync(HttpClient admin, CreateAccessPolicyRuleRequest request) =>
        ReadDataAsync<AccessPolicyRuleDto>(admin.PostAsJsonAsync("/api/access-policies", request));

    private static Task<AccessPolicySimulationResponse> SimulateAsync(HttpClient admin, SimulateAccessPolicyRequest request) =>
        ReadDataAsync<AccessPolicySimulationResponse>(admin.PostAsJsonAsync("/api/access-policies/simulate", request));

    private static async Task<T> ReadDataAsync<T>(Task<HttpResponseMessage> responseTask) =>
        await ReadDataAsync<T>(await responseTask);

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
