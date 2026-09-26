using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>ADM-03: retried creations with the same Idempotency-Key act once.</summary>
public sealed class IdempotencyTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public IdempotencyTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ARetriedCreation_ReplaysTheFirstResponse_AndCreatesOnce()
    {
        using var admin = await CreateAdminClientAsync();
        var applicationId = await AuthCenterApplicationIdAsync();
        var name = $"Idempotent {Guid.NewGuid():N}"[..24];
        var key = Guid.NewGuid().ToString();

        var first = await SendAsync(admin, "/api/roles", new { applicationSystemId = applicationId, name }, key);
        var retry = await SendAsync(admin, "/api/roles", new { applicationSystemId = applicationId, name }, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.True(retry.Headers.TryGetValues("Idempotent-Replayed", out var replayed) && replayed.Single() == "true");
        Assert.Equal(await IdAsync(first), await IdAsync(retry));
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().Roles.CountAsync(role => role.DisplayName == name));

        // Without a key, the same request creates again (here: a conflict on the unique name).
        var withoutKey = await SendAsync(admin, "/api/roles", new { applicationSystemId = applicationId, name }, key: null);
        Assert.False(withoutKey.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task AKeyReusedForADifferentRequest_OrMalformed_IsRefused()
    {
        using var admin = await CreateAdminClientAsync();
        var applicationId = await AuthCenterApplicationIdAsync();
        var key = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(admin, "/api/roles", new { applicationSystemId = applicationId, name = $"Reuse {Guid.NewGuid():N}"[..20] }, key)).StatusCode);

        var reused = await SendAsync(admin, "/api/roles", new { applicationSystemId = applicationId, name = $"Other {Guid.NewGuid():N}"[..20] }, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", (await reused.Content.ReadFromJsonAsync<ApiResponse>())?.ErrorCode);

        var malformed = await SendAsync(admin, "/api/roles", new { applicationSystemId = applicationId, name = "Malformed key" }, "has spaces");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_INVALID", (await malformed.Content.ReadFromJsonAsync<ApiResponse>())?.ErrorCode);
    }

    [Fact]
    public async Task AReplayedSecret_IsTheSameOne_AndIsNotStoredInPlainText()
    {
        using var admin = await CreateAdminClientAsync();
        var applicationId = await AuthCenterApplicationIdAsync();
        var clientId = $"idem-{Guid.NewGuid():N}"[..20];
        var request = new
        {
            applicationSystemId = applicationId, clientId, displayName = "Idempotent client", clientType = 0,
            redirectUris = Array.Empty<string>(), allowedScopes = new[] { "profile" }, grantTypes = new[] { "client_credentials" },
            loginUrl = "https://app.example.test/login", requirePkce = false
        };
        var key = Guid.NewGuid().ToString();

        var first = await DataAsync(await SendAsync(admin, "/api/oauth/clients", request, key));
        var retry = await DataAsync(await SendAsync(admin, "/api/oauth/clients", request, key));

        var secret = first.GetProperty("clientSecret").GetString();
        Assert.False(string.IsNullOrEmpty(secret));
        Assert.Equal(secret, retry.GetProperty("clientSecret").GetString());
        await using var scope = _factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().TransientStates
            .Where(state => state.Purpose == "idempotency_response").Select(state => state.Value).ToListAsync();
        Assert.NotEmpty(stored);
        Assert.DoesNotContain(stored, value => value != null && value.Contains(secret!, StringComparison.Ordinal));
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string path, object body, string? key)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (key is not null)
            message.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return client.SendAsync(message);
    }

    private static async Task<string?> IdAsync(HttpResponseMessage response) => (await DataAsync(response)).GetProperty("id").GetString();

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private async Task<Guid> AuthCenterApplicationIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().ApplicationSystems
            .Where(application => application.Code == DomainConstants.SystemCodes.AuthCenter).Select(application => application.Id).SingleAsync();
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateClient();
        var auth = (await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        })).Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }
}
