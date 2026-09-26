using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Requests.Profiles;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Groups;
using AuthCenter.Contracts.Responses.Lifecycle;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>ADM-05: typed rule operators keep rule-managed groups in step with the universal profile.</summary>
public sealed class DynamicGroupRuleTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public DynamicGroupRuleTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ARule_FillsItsGroupAtOnce_AndEditingItRecomputesTheMembers()
    {
        using var admin = await CreateAdminClientAsync();
        var level = await CreateDefinitionAsync("Integer");
        var department = await CreateDefinitionAsync("String");
        var junior = await CreateUserAsync((level.Id, "2"), (department.Id, "\"Sales\""));
        var senior = await CreateUserAsync((level.Id, "7"), (department.Id, "\"Platform Engineering\""));
        await CreateUserAsync();
        var groupId = await CreateGroupAsync();

        var rule = await ReadDataAsync<DynamicGroupRuleDto>(await CreateRuleAsync(admin, groupId, level.Id, "gte", "5"));
        Assert.Equal("gte", rule.Operator);
        Assert.Equal([senior], await MembersAsync(groupId));
        var preview = await ReadDataAsync<GroupRulePreviewDto>(await admin.PostAsJsonAsync($"/api/lifecycle/group-rules/{rule.Id}/preview", new GroupRulePreviewRequest { Page = 1, PageSize = 10 }));
        Assert.Equal([senior], preview.Users.Items.Select(user => user.Id));

        var updated = await ReadDataAsync<DynamicGroupRuleDto>(await admin.PutAsJsonAsync($"/api/lifecycle/group-rules/{rule.Id}", new UpdateDynamicGroupRuleRequest
        {
            ProfileAttributeDefinitionId = level.Id, Operator = "in", ExpectedValue = Json("[2, 3, 3]"), IsActive = true, Version = rule.Version
        }));
        Assert.Equal("[2,3]", updated.ExpectedValue.GetRawText());
        Assert.Equal([junior], await MembersAsync(groupId));

        // A rule on another attribute is an alternative: either one admits the user.
        await ReadDataAsync<DynamicGroupRuleDto>(await CreateRuleAsync(admin, groupId, department.Id, "contains", "\"engineering\""));
        Assert.Equal(Sorted(junior, senior), await MembersAsync(groupId));
    }

    [Fact]
    public async Task ExistsAndNotEqual_ConsiderOnlyUsersWithAValue()
    {
        using var admin = await CreateAdminClientAsync();
        var hiredOn = await CreateDefinitionAsync("Date");
        var early = await CreateUserAsync((hiredOn.Id, "\"2019-03-01\""));
        var late = await CreateUserAsync((hiredOn.Id, "\"2024-11-15\""));
        await CreateUserAsync();

        var anyDate = await CreateGroupAsync();
        await ReadDataAsync<DynamicGroupRuleDto>(await CreateRuleAsync(admin, anyDate, hiredOn.Id, "exists", "true"));
        Assert.Equal(Sorted(early, late), await MembersAsync(anyDate));

        var notEarly = await CreateGroupAsync();
        await ReadDataAsync<DynamicGroupRuleDto>(await CreateRuleAsync(admin, notEarly, hiredOn.Id, "ne", "\"2019-03-01\""));
        Assert.Equal([late], await MembersAsync(notEarly));

        var before2020 = await CreateGroupAsync();
        await ReadDataAsync<DynamicGroupRuleDto>(await CreateRuleAsync(admin, before2020, hiredOn.Id, "lt", "\"2020-01-01\""));
        Assert.Equal([early], await MembersAsync(before2020));
    }

    [Theory]
    [InlineData("Integer", "regex", "\"1\"", "INVALID_GROUP_RULE_OPERATOR")]
    [InlineData("Integer", "contains", "\"1\"", "INVALID_GROUP_RULE_OPERATOR")]
    [InlineData("String", "gt", "\"b\"", "INVALID_GROUP_RULE_OPERATOR")]
    [InlineData("Boolean", "startsWith", "\"t\"", "INVALID_GROUP_RULE_OPERATOR")]
    [InlineData("Integer", "gt", "\"many\"", "INVALID_GROUP_RULE_VALUE")]
    [InlineData("Integer", "in", "[]", "INVALID_GROUP_RULE_VALUE")]
    [InlineData("Integer", "in", "5", "INVALID_GROUP_RULE_VALUE")]
    [InlineData("String", "exists", "false", "INVALID_GROUP_RULE_VALUE")]
    [InlineData("String", "startsWith", "\"\"", "INVALID_GROUP_RULE_VALUE")]
    [InlineData("Date", "gte", "\"yesterday\"", "INVALID_GROUP_RULE_VALUE")]
    public async Task ARuleWhoseOperatorOrValueDoesNotFitTheAttribute_IsRefused(string dataType, string op, string expected, string code)
    {
        using var admin = await CreateAdminClientAsync();
        var definition = await CreateDefinitionAsync(dataType);
        var groupId = await CreateGroupAsync();

        var response = await CreateRuleAsync(admin, groupId, definition.Id, op, expected);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        Assert.True(code == JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString(), body);
    }

    [Fact]
    public async Task ProfileChanges_MoveUsersInAndOut_AndLeavingEndsTheirSessions()
    {
        using var admin = await CreateAdminClientAsync();
        var department = await CreateDefinitionAsync("String");
        var userId = await CreateUserAsync((department.Id, "\"Platform\""));
        var groupId = await CreateGroupAsync();
        await ReadDataAsync<DynamicGroupRuleDto>(await CreateRuleAsync(admin, groupId, department.Id, "startsWith", "\"plat\""));
        Assert.Equal([userId], await MembersAsync(groupId));
        var sessionId = await AddSessionAsync(userId);

        await UpdateProfileAsync(admin, userId, department.Key, "\"Sales\"");
        Assert.Empty(await MembersAsync(groupId));
        Assert.NotNull(await RevokedAtAsync(sessionId));

        await UpdateProfileAsync(admin, userId, department.Key, "\"Platform Operations\"");
        Assert.Equal([userId], await MembersAsync(groupId));
    }

    [Fact]
    public async Task ARuleManagedGroup_RefusesManualMembers_UntilItsRulesAreGone()
    {
        using var admin = await CreateAdminClientAsync();
        var department = await CreateDefinitionAsync("String");
        var member = await CreateUserAsync((department.Id, "\"Finance\""));
        var outsider = await CreateUserAsync();
        var groupId = await CreateGroupAsync();
        var rule = await ReadDataAsync<DynamicGroupRuleDto>(await CreateRuleAsync(admin, groupId, department.Id, "eq", "\"Finance\""));

        Assert.True((await ReadDataAsync<DirectoryGroupDto>(await admin.GetAsync($"/api/groups/{groupId}"))).IsRuleManaged);
        var added = await admin.PostAsync($"/api/groups/{groupId}/members/{outsider}", null);
        Assert.Equal(HttpStatusCode.BadRequest, added.StatusCode);
        Assert.Equal("GROUP_MANAGED_BY_RULES", (await added.Content.ReadFromJsonAsync<ApiResponse>())?.ErrorCode);
        var removed = await admin.DeleteAsync($"/api/groups/{groupId}/members/{member}");
        Assert.Equal("GROUP_MANAGED_BY_RULES", (await removed.Content.ReadFromJsonAsync<ApiResponse>())?.ErrorCode);

        // Without active rules the group keeps its members and is managed by hand again.
        await ReadDataAsync<DynamicGroupRuleDto>(await admin.PutAsJsonAsync($"/api/lifecycle/group-rules/{rule.Id}", new UpdateDynamicGroupRuleRequest
        {
            ProfileAttributeDefinitionId = department.Id, Operator = "eq", ExpectedValue = Json("\"Finance\""), IsActive = false, Version = rule.Version
        }));
        Assert.Equal([member], await MembersAsync(groupId));
        Assert.False((await ReadDataAsync<DirectoryGroupDto>(await admin.GetAsync($"/api/groups/{groupId}"))).IsRuleManaged);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/groups/{groupId}/members/{outsider}", null)).StatusCode);
        Assert.Equal(Sorted(member, outsider), await MembersAsync(groupId));
    }

    private static Task<HttpResponseMessage> CreateRuleAsync(HttpClient admin, Guid groupId, Guid definitionId, string op, string expectedJson) =>
        admin.PostAsJsonAsync("/api/lifecycle/group-rules", new CreateDynamicGroupRuleRequest
        {
            DirectoryGroupId = groupId, ProfileAttributeDefinitionId = definitionId, Operator = op, ExpectedValue = Json(expectedJson)
        });

    private static async Task UpdateProfileAsync(HttpClient admin, Guid userId, string key, string valueJson)
    {
        var response = await admin.PutAsJsonAsync($"/api/users/{userId}/profile", new UpdateUserProfileRequest
        {
            Attributes = new Dictionary<string, JsonElement?> { [key] = Json(valueJson) }
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(Guid Id, string Key)> CreateDefinitionAsync(string dataType)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IUserProfileService>().CreateDefinitionAsync(new CreateProfileAttributeDefinitionRequest
        {
            Key = $"rule-{Guid.NewGuid():N}"[..24], DisplayName = $"Rule {dataType}", DataType = dataType
        });
        Assert.True(created.IsSuccess, created.Message);
        return (created.Data!.Id, created.Data.Key);
    }

    private async Task<Guid> CreateUserAsync(params (Guid DefinitionId, string ValueJson)[] values)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var email = $"rules-{Guid.NewGuid():N}@example.com";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), FullName = "Rule Subject", Email = email, NormalizedEmail = email.ToUpperInvariant(),
            UserName = email, NormalizedUserName = email.ToUpperInvariant(), EmailConfirmed = true, IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        foreach (var (definitionId, valueJson) in values)
            db.UserProfileAttributeValues.Add(new UserProfileAttributeValue { UserId = user.Id, AttributeDefinitionId = definitionId, ValueJson = valueJson, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> CreateGroupAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var name = $"Rule group {Guid.NewGuid():N}";
        var group = new DirectoryGroup { Id = Guid.NewGuid(), Name = name, NormalizedName = name.ToUpperInvariant(), IsActive = true, CreatedAt = DateTime.UtcNow };
        db.DirectoryGroups.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    }

    private async Task<Guid> AddSessionAsync(Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(), UserId = userId, ApplicationCode = DomainConstants.SystemCodes.AuthCenter, TokenHash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1)
        };
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync();
        return token.Id;
    }

    private async Task<DateTime?> RevokedAtAsync(Guid sessionId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().RefreshTokens.AsNoTracking()
            .Where(token => token.Id == sessionId).Select(token => token.RevokedAt).SingleAsync();
    }

    private async Task<List<Guid>> MembersAsync(Guid groupId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var members = await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().UserGroupMemberships.AsNoTracking()
            .Where(membership => membership.GroupId == groupId).Select(membership => membership.UserId).ToListAsync();
        return [.. members.Order()];
    }

    private static List<Guid> Sorted(params Guid[] ids) => [.. ids.Order()];

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateClient();
        var auth = await ReadDataAsync<AuthResponse>(await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response) where T : class
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        var result = JsonSerializer.Deserialize<ApiResponse<T>>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(result?.Data);
        return result.Data;
    }
}
