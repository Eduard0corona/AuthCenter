using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Users;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Users;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

public sealed class UserProvisioningTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;
    public UserProvisioningTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_WithTemporaryPassword_RequiresChangeAndAuditsWithoutSecret()
    {
        using var admin = await CreateAdminClientAsync();
        var password = TestSecretGenerator.CreatePassword();
        var response = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            FullName = "Temporary Password User",
            Email = $"temporary-{Guid.NewGuid():N}@example.com",
            Password = password,
            IsTemporaryPassword = true
        });
        var rawResponse = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<UserDto>>();

        Assert.NotNull(body?.Data);
        Assert.True(body.Data.HasLocalPassword);
        Assert.True(body.Data.MustChangePassword);
        Assert.DoesNotContain(password, rawResponse, StringComparison.Ordinal);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var audit = await db.AuditLogs.AsNoTracking()
            .Where(entry => entry.Action == "USER_CREATED" && entry.EntityId == body.Data.Id.ToString())
            .SingleAsync();
        Assert.DoesNotContain(password, audit.MetadataJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_TemporaryPasswordFlagWithoutPassword_IsRejected()
    {
        using var admin = await CreateAdminClientAsync();
        var response = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            FullName = "Invalid Temporary User",
            Email = $"invalid-temporary-{Guid.NewGuid():N}@example.com",
            IsTemporaryPassword = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var weakResponse = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            FullName = "Weak Temporary User",
            Email = $"weak-temporary-{Guid.NewGuid():N}@example.com",
            Password = "Weak1234",
            IsTemporaryPassword = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, weakResponse.StatusCode);
    }

    [Fact]
    public async Task Invite_ReturnsUserMetadataWithoutInvitationToken()
    {
        using var admin = await CreateAdminClientAsync();
        var applicationId = await GetAuthCenterApplicationIdAsync();
        var response = await admin.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest
        {
            FullName = "Invited Directory User",
            Email = $"invited-directory-{Guid.NewGuid():N}@example.com",
            ApplicationSystemId = applicationId,
            GrantActiveAccess = false
        });
        var rawResponse = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<UserDto>>();

        Assert.NotNull(body?.Data);
        Assert.False(body.Data.HasLocalPassword);
        Assert.Contains(body.Data.ApplicationAccesses, access => access.ApplicationId == applicationId && !access.IsActive);
        Assert.DoesNotContain("token", rawResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", rawResponse, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Users_CanBeSortedByAnAllowListedServerField()
    {
        using var admin = await CreateAdminClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        foreach (var ending in new[] { "Alpha", "Zulu" })
        {
            var response = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
            {
                FullName = $"Directory Sort {suffix} {ending}",
                Email = $"sort-{suffix}-{ending.ToLowerInvariant()}@example.com",
                Password = TestSecretGenerator.CreatePassword()
            });
            response.EnsureSuccessStatusCode();
        }

        var responseBody = await admin.GetFromJsonAsync<ApiResponse<PagedResult<UserDto>>>(
            $"/api/users?search=Directory%20Sort%20{suffix}&sortBy=fullName&sortDirection=desc&pageSize=20");

        Assert.NotNull(responseBody?.Data);
        Assert.Equal(2, responseBody.Data.TotalCount);
        Assert.EndsWith("Zulu", responseBody.Data.Items[0].FullName, StringComparison.Ordinal);
        Assert.EndsWith("Alpha", responseBody.Data.Items[1].FullName, StringComparison.Ordinal);
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Data!.AccessToken);
        return client;
    }

    private async Task<Guid> GetAuthCenterApplicationIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.ApplicationSystems.Where(application => application.Code == DomainConstants.SystemCodes.AuthCenter).Select(application => application.Id).SingleAsync();
    }
}
