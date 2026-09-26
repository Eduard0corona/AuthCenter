using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
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

/// <summary>Readiness on SQL Server: an instance is not ready while migrations its build needs are missing.</summary>
public class SchemaReadinessRelationalTests
{
    [RelationalFact]
    public async Task Readiness_FailsWhileMigrationsArePending_AndIsDegradedByUnknownOnes()
    {
        await using var factory = new SqlServerWebApplicationFactory(new Dictionary<string, string> { ["HealthChecks:ReadinessHost"] = "localhost" });
        using var client = factory.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await CheckStatusAsync(ready, "database-schema"));

        // The newest migration missing, as when code is deployed before its migrations.
        string latest;
        await using (var connection = new SqlConnection(factory.ConnectionString))
        {
            await connection.OpenAsync();
            await using var read = new SqlCommand("SELECT TOP (1) MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC", connection);
            latest = (string)(await read.ExecuteScalarAsync())!;
            await using var delete = new SqlCommand("DELETE FROM __EFMigrationsHistory WHERE MigrationId = @id", connection);
            delete.Parameters.AddWithValue("@id", latest);
            await delete.ExecuteNonQueryAsync();
        }
        var pending = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
        Assert.Equal("Unhealthy", await CheckStatusAsync(pending, "database-schema"));
        Assert.Contains(latest, await pending.Content.ReadAsStringAsync());

        // Restored, plus one this build does not know (a rollback to an older build): degraded, still routable.
        await using (var connection = new SqlConnection(factory.ConnectionString))
        {
            await connection.OpenAsync();
            await using var restore = new SqlCommand("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@id, '10.0.10'), ('29991231000000_FromANewerBuild', '10.0.10')", connection);
            restore.Parameters.AddWithValue("@id", latest);
            await restore.ExecuteNonQueryAsync();
        }
        var degraded = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, degraded.StatusCode);
        Assert.Equal("Degraded", await CheckStatusAsync(degraded, "database-schema"));
    }

    private static async Task<string?> CheckStatusAsync(HttpResponseMessage response, string name)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == name)
            .GetProperty("status").GetString();
    }
}
