using System.Net;
using System.Net.Http.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// GitHub login is the one social provider that authenticates by calling the provider's API rather
/// than by validating a signed token, so it is the only one that can be exercised end to end with
/// the outbound call stubbed.
/// </summary>
public class GitHubLoginTests
{
    [Fact]
    public async Task GitHubLogin_SignsInAnExistingUser_AndRecordsTheProviderLink()
    {
        var email = $"github-{Guid.NewGuid():N}@example.com";
        using var factory = new GitHubStubWebApplicationFactory(GitHubUserResponding(email, id: 4242));
        using var client = factory.CreateClient();

        await AllowGitHubLoginAsync(factory);
        var userId = await CreateUserAsync(factory, email);

        var localLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = "GitHubUser12345",
            ApplicationCode = "AUTHCENTER"
        });
        var localAuth = await ReadDataAsync<AuthResponse>(localLogin);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", localAuth.AccessToken);
        var linkResponse = await client.PostAsJsonAsync("/api/auth/external-providers/link", new LinkExternalProviderRequest
        {
            Provider = "GitHub",
            Credential = "stubbed-access-token"
        });
        Assert.Equal(HttpStatusCode.OK, linkResponse.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;

        var response = await client.PostAsJsonAsync("/api/auth/github", new GitHubLoginRequest
        {
            AccessToken = "stubbed-access-token",
            ApplicationCode = "AUTHCENTER"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = await ReadDataAsync<AuthResponse>(response);
        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));
        Assert.Equal(email, auth.User.Email);
        Assert.Contains("AUTHCENTER", auth.User.Applications);

        var link = await GetProviderLinkAsync(factory, userId);
        Assert.Equal("GitHub", link.Provider);
        Assert.Equal("4242", link.ProviderUserId);
        Assert.True(link.IsActive);
    }

    [Fact]
    public async Task GitHubLogin_FallsBackToTheVerifiedPrimaryEmail()
    {
        var email = $"github-primary-{Guid.NewGuid():N}@example.com";
        using var factory = new GitHubStubWebApplicationFactory(
            GitHubUserRespondingWithoutEmail(primaryEmail: email, id: 777));
        using var client = factory.CreateClient();

        await AllowGitHubLoginAsync(factory);
        await CreateUserAsync(factory, email);

        var response = await client.PostAsJsonAsync("/api/auth/github", new GitHubLoginRequest
        {
            AccessToken = "stubbed-access-token",
            ApplicationCode = "AUTHCENTER"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("EXTERNAL_ACCOUNT_LINK_REQUIRED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task GitHubLogin_WithATokenGitHubRejects_IsUnauthorized()
    {
        using var factory = new GitHubStubWebApplicationFactory(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = factory.CreateClient();

        await AllowGitHubLoginAsync(factory);

        var response = await client.PostAsJsonAsync("/api/auth/github", new GitHubLoginRequest
        {
            AccessToken = "revoked-access-token",
            ApplicationCode = "AUTHCENTER"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_GITHUB_TOKEN", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task GitHubLogin_WithNoVerifiedEmail_IsRejected()
    {
        using var factory = new GitHubStubWebApplicationFactory(GitHubUserRespondingWithoutAnyEmail(id: 9));
        using var client = factory.CreateClient();

        await AllowGitHubLoginAsync(factory);

        var response = await client.PostAsJsonAsync("/api/auth/github", new GitHubLoginRequest
        {
            AccessToken = "stubbed-access-token",
            ApplicationCode = "AUTHCENTER"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("GITHUB_EMAIL_NOT_AVAILABLE", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task GitHubLogin_WhenTheApplicationDoesNotAllowIt_IsRejected()
    {
        var email = $"github-off-{Guid.NewGuid():N}@example.com";
        using var factory = new GitHubStubWebApplicationFactory(GitHubUserResponding(email, id: 31));
        using var client = factory.CreateClient();

        // AllowGitHubLogin is left off, which is how the AUTHCENTER application is seeded.
        await CreateUserAsync(factory, email);

        var response = await client.PostAsJsonAsync("/api/auth/github", new GitHubLoginRequest
        {
            AccessToken = "stubbed-access-token",
            ApplicationCode = "AUTHCENTER"
        });

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("GITHUB_LOGIN_DISABLED", await ReadErrorCodeAsync(response));
    }

    // --- Stub responses ---

    private static Func<HttpRequestMessage, HttpResponseMessage> GitHubUserResponding(string email, long id)
        => request => request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
            ? Json($$"""{"id":{{id}},"login":"octocat","name":"Octo Cat","avatar_url":"https://github.test/a.png","email":"{{email}}"}""")
            : Json("[]");

    private static Func<HttpRequestMessage, HttpResponseMessage> GitHubUserRespondingWithoutEmail(
        string primaryEmail,
        long id)
        => request => request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
            ? Json($$"""{"id":{{id}},"login":"octocat","name":"Octo Cat","email":null}""")
            : Json($$"""[{"email":"noisy@example.com","primary":false,"verified":true},{"email":"{{primaryEmail}}","primary":true,"verified":true}]""");

    private static Func<HttpRequestMessage, HttpResponseMessage> GitHubUserRespondingWithoutAnyEmail(long id)
        => request => request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
            ? Json($$"""{"id":{{id}},"login":"octocat","name":"Octo Cat","email":null}""")
            // An unverified address must not be accepted as an identity.
            : Json("""[{"email":"unverified@example.com","primary":true,"verified":false}]""");

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    // --- Helpers ---

    private static async Task AllowGitHubLoginAsync(GitHubStubWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();

        var settings = await db.ApplicationRegistrationSettings
            .SingleAsync(s => s.ApplicationSystem.Code == "AUTHCENTER");
        settings.AllowGitHubLogin = true;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> CreateUserAsync(GitHubStubWebApplicationFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var application = await db.ApplicationSystems.SingleAsync(a => a.Code == "AUTHCENTER");
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = "Octo Cat",
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            HasLocalPassword = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var result = await userManager.CreateAsync(user, "GitHubUser12345");
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

        return user.Id;
    }

    private static async Task<ExternalIdentityProvider> GetProviderLinkAsync(
        GitHubStubWebApplicationFactory factory,
        Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.ExternalIdentityProviders.AsNoTracking().SingleAsync(p => p.UserId == userId);
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
        => (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode;

    /// <summary>
    /// Configures a GitHub client id — without one the provider is treated as unconfigured — and
    /// swaps the outbound handler of the named "GitHub" client for a stub.
    /// </summary>
    private sealed class GitHubStubWebApplicationFactory : AuthCenterWebApplicationFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public GitHubStubWebApplicationFactory(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:GitHub:ClientId"] = "stub-github-client-id"
                }));

            builder.ConfigureServices(services =>
                services.AddHttpClient("GitHub")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_respond)));
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_respond(request));
    }
}
