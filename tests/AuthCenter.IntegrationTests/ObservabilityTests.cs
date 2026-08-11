using System.Net.Http.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

public sealed class ObservabilityTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;
    public ObservabilityTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task SystemLog_CapturesW3cTraceIdWithoutTokenOrCredentialMaterial()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        response.EnsureSuccessStatusCode();

        await using var scope = _factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().AuditLogs
            .AsNoTracking().Where(item => item.Action == "LOGIN_SUCCESS")
            .OrderByDescending(item => item.CreatedAt).FirstAsync();
        Assert.Matches("^[a-f0-9]{32}$", audit.TraceId!);
        Assert.DoesNotContain(AuthCenterWebApplicationFactory.AdminPassword, audit.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", audit.MetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
