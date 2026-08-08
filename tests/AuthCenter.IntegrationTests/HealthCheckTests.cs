using System.Net;
using Xunit;

namespace AuthCenter.IntegrationTests;

public class HealthCheckTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthCheckTests(AuthCenterWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task HealthEndpoint_Responds()
    {
        var response = await _client.GetAsync("/health/live");

        // Health endpoint returns 200 (Healthy) or 503 (Degraded/Unhealthy when no DB in CI).
        // Either is a valid response from the health check middleware — not a 404 or crash.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
