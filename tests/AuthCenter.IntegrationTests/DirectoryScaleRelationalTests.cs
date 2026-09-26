using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// The administrative reads that grow with the directory, against a directory seeded with
/// ops/load/seed-large-directory.sql (20,000 users by default, AUTHCENTER_SCALE_USERS to change
/// it). Each read must answer within its budget; the timings are written to the test output.
/// Runs with AUTHCENTER_SCALE_TESTS=1 (the Scale workflow), not in every CI run.
/// </summary>
public sealed class DirectoryScaleRelationalTests(ITestOutputHelper output)
{
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SnapshotBudget = TimeSpan.FromSeconds(90);

    [ScaleFact]
    [Trait("Category", "Scale")]
    public async Task ALargeDirectory_KeepsTheAdministrativeReadsWithinBudget()
    {
        var users = int.TryParse(Environment.GetEnvironmentVariable("AUTHCENTER_SCALE_USERS"), out var configured) && configured > 0 ? configured : 20_000;
        var groups = Math.Max(10, users / 100);
        await using var factory = new SqlServerWebApplicationFactory();
        using var admin = await CreateAdminClientAsync(factory);

        var code = $"SCALE{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var applicationId = await DataIdAsync(await admin.PostAsJsonAsync("/api/applications", new { code, name = $"Escala {code}", registrationMode = "Closed", allowPasswordLogin = true }));
        var script = await File.ReadAllTextAsync(RepositoryFile("ops/load/seed-large-directory.sql"));
        script = Regex.Replace(script, @"DECLARE @Users int = \d+;", $"DECLARE @Users int = {users};");
        script = Regex.Replace(script, @"DECLARE @Groups int = \d+;", $"DECLARE @Groups int = {groups};");
        script = Regex.Replace(script, @"DECLARE @ApplicationCode nvarchar\(50\) = N'\w+';", $"DECLARE @ApplicationCode nvarchar(50) = N'{code}';");
        var seeding = Stopwatch.StartNew();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(15));
            await db.Database.ExecuteSqlRawAsync(script);
        }
        seeding.Stop();
        output.WriteLine($"Seeded {users:N0} users and {groups:N0} groups in {seeding.Elapsed.TotalSeconds:N1} s.");

        Guid groupId, firstGroupId, secondGroupId, userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            var seededGroups = await db.DirectoryGroups.Where(group => group.NormalizedName.StartsWith("SCALE GROUP ")).OrderBy(group => group.NormalizedName).Select(group => group.Id).Take(3).ToListAsync();
            (groupId, firstGroupId, secondGroupId) = (seededGroups[0], seededGroups[1], seededGroups[2]);
            userId = await db.Users.Where(user => user.NormalizedEmail == $"SCALE-{users / 2}@LOAD.TEST").Select(user => user.Id).SingleAsync();
        }

        // Two incompatible roles held through two of the seeded groups: the violations list has work to do.
        var payer = await DataIdAsync(await admin.PostAsJsonAsync("/api/roles", new { applicationSystemId = applicationId, name = "Pagador" }));
        var approver = await DataIdAsync(await admin.PostAsJsonAsync("/api/roles", new { applicationSystemId = applicationId, name = "Aprobador" }));
        await AssertOkAsync(await admin.PutAsJsonAsync($"/api/groups/{firstGroupId}/access", new { applicationSystemIds = new[] { applicationId }, roleIds = new[] { payer } }));
        await AssertOkAsync(await admin.PutAsJsonAsync($"/api/groups/{secondGroupId}/access", new { applicationSystemIds = new[] { applicationId }, roleIds = new[] { approver } }));
        await AssertOkAsync(await admin.PostAsJsonAsync("/api/governance/sod-rules", new { name = "Escala", firstRoleId = payer, secondRoleId = approver, isActive = true }));

        var lastPage = (users + 49) / 50;
        var timings = new List<(string Operation, TimeSpan Elapsed)>
        {
            await MeasureAsync("Users, first page", () => admin.GetAsync("/api/users?page=1&pageSize=50")),
            await MeasureAsync("Users, last page", () => admin.GetAsync($"/api/users?page={lastPage}&pageSize=50")),
            await MeasureAsync("Users, search by email", () => admin.GetAsync($"/api/users?search=scale-{users / 2}%40&pageSize=20")),
            await MeasureAsync("Users of the application", () => admin.GetAsync($"/api/users?applicationSystemId={applicationId}&pageSize=50")),
            await MeasureAsync("User detail", () => admin.GetAsync($"/api/users/{userId}")),
            await MeasureAsync("Groups, search", () => admin.GetAsync("/api/groups?search=scale%20group&pageSize=50")),
            await MeasureAsync("Group members", () => admin.GetAsync($"/api/groups/{groupId}/members?page=1&pageSize=50")),
            await MeasureAsync("Dashboard", () => admin.GetAsync("/api/admin-dashboard")),
            await MeasureAsync("Separation of duties violations", () => admin.GetAsync("/api/governance/sod-violations?pageSize=50"))
        };
        var review = Stopwatch.StartNew();
        var campaignId = await DataIdAsync(await admin.PostAsJsonAsync("/api/governance/access-reviews", new { name = "Escala", applicationSystemId = applicationId, dueAt = DateTime.UtcNow.AddDays(7) }));
        review.Stop();
        timings.Add(("Access review of the whole application (snapshot)", review.Elapsed));
        timings.Add(await MeasureAsync("Access review items, first page", () => admin.GetAsync($"/api/governance/access-reviews/{campaignId}/items?pageSize=50")));

        var table = new StringBuilder()
            .AppendLine($"| Operation ({users:N0} users, {groups:N0} groups) | ms |")
            .AppendLine("|---|---:|");
        foreach (var (operation, elapsed) in timings)
            table.AppendLine($"| {operation} | {elapsed.TotalMilliseconds:N0} |");
        output.WriteLine(table.ToString());
        // On GitHub Actions the table also goes to the run summary.
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
            await File.AppendAllTextAsync(summary, $"## Directory scale\n\nSeeded in {seeding.Elapsed.TotalSeconds:N1} s.\n\n{table}\n");

        Assert.True(review.Elapsed < SnapshotBudget, $"The access review snapshot took {review.Elapsed}.");
        var slow = timings.Where(timing => !timing.Operation.StartsWith("Access review of", StringComparison.Ordinal) && timing.Elapsed > ReadBudget).ToList();
        Assert.True(slow.Count == 0, "Over budget: " + string.Join(", ", slow.Select(timing => $"{timing.Operation} {timing.Elapsed.TotalMilliseconds:N0} ms")));
    }

    /// <summary>The second call's time: the first one compiles the query and warms the pool.</summary>
    private static async Task<(string, TimeSpan)> MeasureAsync(string operation, Func<Task<HttpResponseMessage>> call)
    {
        (await call()).EnsureSuccessStatusCode();
        var stopwatch = Stopwatch.StartNew();
        using var response = await call();
        stopwatch.Stop();
        response.EnsureSuccessStatusCode();
        return (operation, stopwatch.Elapsed);
    }

    private static async Task<HttpClient> CreateAdminClientAsync(SqlServerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = SqlServerWebApplicationFactory.AdminEmail,
            Password = SqlServerWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<Guid> DataIdAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        using var body = JsonDocument.Parse(text);
        return body.RootElement.GetProperty("data").GetProperty("id").GetGuid();
    }

    private static async Task AssertOkAsync(HttpResponseMessage response) =>
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static string RepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"{relativePath} was not found above {AppContext.BaseDirectory}.");
    }
}

/// <summary>A directory scale test: runs with AUTHCENTER_SCALE_TESTS=1 and a SQL Server.</summary>
public sealed class ScaleFactAttribute : FactAttribute
{
    public ScaleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AUTHCENTER_SCALE_TESTS") != "1")
            Skip = "Directory scale test: set AUTHCENTER_SCALE_TESTS=1 with AUTHCENTER_RELATIONAL_TEST_CONNECTION.";
        else if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUTHCENTER_RELATIONAL_TEST_CONNECTION")))
            Skip = "Requires SQL Server: set AUTHCENTER_RELATIONAL_TEST_CONNECTION.";
    }
}
