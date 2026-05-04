using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.OAuth;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AuthCenter.IntegrationTests;

public class OAuthFlowTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public OAuthFlowTests(AuthCenterWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OAuthClient_Create_And_Retrieve()
    {
        using var client = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var clientId = $"test-{Guid.NewGuid():N}"[..20];
        var createRequest = new CreateOAuthClientRequest
        {
            ClientId = clientId,
            DisplayName = "Test Confidential App",
            ClientType = (int)OAuthClientType.Confidential,
            RedirectUris = ["https://myapp.com/callback"],
            AllowedScopes = ["openid", "email"],
            GrantTypes = ["authorization_code", "refresh_token"],
            LoginUrl = "https://myapp.com/login",
            AccessTokenLifetimeSeconds = 3600,
            RequirePkce = true
        };

        var createResponse = await client.PostAsJsonAsync("/api/oauth/clients", createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await ReadDataAsync<OAuthClientCreatedResponse>(createResponse);
        Assert.NotNull(created.ClientSecret);
        Assert.Equal(clientId, created.Client.ClientId);
        Assert.Equal((int)OAuthClientType.Confidential, created.Client.ClientType);
        Assert.True(created.Client.IsActive);

        var getResponse = await client.GetAsync($"/api/oauth/clients/{clientId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var retrieved = await ReadDataAsync<OAuthClientResponse>(getResponse);
        Assert.Equal("Test Confidential App", retrieved.DisplayName);
        Assert.Equal(clientId, retrieved.ClientId);
    }

    [Fact]
    public async Task OAuthClient_Update_And_Deactivate()
    {
        using var client = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var clientId = $"upd-{Guid.NewGuid():N}"[..20];
        await CreateOAuthClientAsync(client, clientId);

        var updateRequest = new UpdateOAuthClientRequest
        {
            DisplayName = "Updated Name",
            RedirectUris = ["https://myapp.com/callback", "https://myapp.com/callback2"],
            AllowedScopes = ["openid", "profile", "email"],
            GrantTypes = ["authorization_code"],
            LoginUrl = "https://myapp.com/login",
            AccessTokenLifetimeSeconds = 1800,
            RequirePkce = true,
            IsActive = true
        };

        var updateResponse = await client.PutAsJsonAsync($"/api/oauth/clients/{clientId}", updateRequest);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await ReadDataAsync<OAuthClientResponse>(updateResponse);
        Assert.Equal("Updated Name", updated.DisplayName);
        Assert.Equal(1800, updated.AccessTokenLifetimeSeconds);

        var deactivateResponse = await client.DeleteAsync($"/api/oauth/clients/{clientId}");
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);

        var getAfterDeactivate = await ReadDataAsync<OAuthClientResponse>(
            await client.GetAsync($"/api/oauth/clients/{clientId}"));
        Assert.False(getAfterDeactivate.IsActive);
    }

    [Fact]
    public async Task RotateSecret_ReturnsNewSecret()
    {
        using var client = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var clientId = $"rot-{Guid.NewGuid():N}"[..20];
        var (_, originalSecret) = await CreateOAuthClientAsync(client, clientId);

        var rotateResponse = await client.PostAsync($"/api/oauth/clients/{clientId}/rotate-secret", null);
        Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);
        var rotated = await ReadDataAsync<RotateClientSecretResponse>(rotateResponse);

        Assert.False(string.IsNullOrWhiteSpace(rotated.ClientSecret));
        Assert.NotEqual(originalSecret, rotated.ClientSecret);
    }

    [Fact]
    public async Task AuthorizationCodeFlow_WithPkce_IssuesTokens()
    {
        using var adminClient = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var clientId = $"flow-{Guid.NewGuid():N}"[..20];
        var (_, clientSecret) = await CreatePublicOAuthClientAsync(adminClient, clientId);

        var (verifier, challenge) = GeneratePkce();

        var noRedirectClient = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var authorizeUrl = $"/oauth/authorize?response_type=code&client_id={clientId}" +
                           $"&redirect_uri=https://myapp.com/callback" +
                           $"&scope=openid+email+offline_access" +
                           $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                           $"&code_challenge_method=S256" +
                           $"&state=xyz123";

        var authorizeResponse = await noRedirectClient.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);

        var loginUrl = authorizeResponse.Headers.Location!.ToString();
        Assert.Contains("interaction_id=", loginUrl);
        var interactionId = ExtractQueryParam(loginUrl, "interaction_id");
        Assert.False(string.IsNullOrWhiteSpace(interactionId));

        var authClient = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        authClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var completeResponse = await authClient.PostAsJsonAsync("/oauth/authorize/complete", new CompleteAuthorizationRequest
        {
            InteractionId = interactionId,
            Consent = true
        });
        Assert.Equal(HttpStatusCode.Redirect, completeResponse.StatusCode);

        var redirectLocation = completeResponse.Headers.Location!.ToString();
        Assert.Contains("code=", redirectLocation);
        Assert.Contains("state=xyz123", redirectLocation);
        var code = Uri.UnescapeDataString(ExtractQueryParam(redirectLocation, "code"));

        var tokenClient = _factory.CreateClient();
        var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://myapp.com/callback",
            ["client_id"] = clientId,
            ["code_verifier"] = verifier
        });

        var tokenResponse = await tokenClient.PostAsync("/oauth/token", tokenContent);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);

        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<OAuthTokenResponse>();
        Assert.NotNull(tokenBody);
        Assert.False(string.IsNullOrWhiteSpace(tokenBody.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokenBody.IdToken));
        Assert.False(string.IsNullOrWhiteSpace(tokenBody.RefreshToken));
        Assert.Contains("openid", tokenBody.Scope);
    }

    [Fact]
    public async Task AuthorizationCode_SingleUse_RejectsSecondExchange()
    {
        using var adminClient = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var clientId = $"reu-{Guid.NewGuid():N}"[..20];
        await CreatePublicOAuthClientAsync(adminClient, clientId);

        var (verifier, challenge) = GeneratePkce();
        var code = await GetAuthorizationCodeAsync(adminClient, adminToken, clientId, challenge);

        var tokenContent1 = BuildTokenContent(clientId, code, verifier);
        var tokenResponse1 = await _factory.CreateClient().PostAsync("/oauth/token", tokenContent1);
        Assert.Equal(HttpStatusCode.OK, tokenResponse1.StatusCode);

        var tokenContent2 = BuildTokenContent(clientId, code, verifier);
        var tokenResponse2 = await _factory.CreateClient().PostAsync("/oauth/token", tokenContent2);
        Assert.Equal(HttpStatusCode.BadRequest, tokenResponse2.StatusCode);

        var errorBody = await tokenResponse2.Content.ReadFromJsonAsync<JsonElement>();
        var error = errorBody.GetProperty("error").GetString();
        Assert.Equal("code_already_used", error);
    }

    [Fact]
    public async Task PKCE_InvalidVerifier_Rejected()
    {
        using var adminClient = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var clientId = $"pkce-{Guid.NewGuid():N}"[..20];
        await CreatePublicOAuthClientAsync(adminClient, clientId);

        var (_, challenge) = GeneratePkce();
        var code = await GetAuthorizationCodeAsync(adminClient, adminToken, clientId, challenge);

        var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://myapp.com/callback",
            ["client_id"] = clientId,
            ["code_verifier"] = "invalid-verifier-that-does-not-match-the-challenge"
        });

        var tokenResponse = await _factory.CreateClient().PostAsync("/oauth/token", tokenContent);
        Assert.Equal(HttpStatusCode.BadRequest, tokenResponse.StatusCode);

        var errorBody = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_code_verifier", errorBody.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ClientCredentials_IssuesAccessToken()
    {
        using var adminClient = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var clientId = $"cc-{Guid.NewGuid():N}"[..20];
        var createReq = new CreateOAuthClientRequest
        {
            ClientId = clientId,
            DisplayName = "Machine Client",
            ClientType = (int)OAuthClientType.Confidential,
            RedirectUris = [],
            AllowedScopes = ["openid", "email"],
            GrantTypes = ["client_credentials"],
            LoginUrl = "https://internal.service/",
            RequirePkce = false
        };
        var createResp = await adminClient.PostAsJsonAsync("/api/oauth/clients", createReq);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await ReadDataAsync<OAuthClientCreatedResponse>(createResp);
        var secret = created.ClientSecret!;

        var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = "email"
        });

        var tokenResponse = await _factory.CreateClient().PostAsync("/oauth/token", tokenContent);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);

        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<OAuthTokenResponse>();
        Assert.NotNull(tokenBody);
        Assert.False(string.IsNullOrWhiteSpace(tokenBody.AccessToken));
        Assert.Null(tokenBody.RefreshToken);
        Assert.Null(tokenBody.IdToken);
        Assert.Contains("email", tokenBody.Scope);
    }

    [Fact]
    public async Task OAuthRefreshToken_RotatesSuccessfully()
    {
        using var adminClient = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var clientId = $"rt-{Guid.NewGuid():N}"[..20];
        await CreatePublicOAuthClientAsync(adminClient, clientId);

        var (verifier, challenge) = GeneratePkce();
        var code = await GetAuthorizationCodeAsync(adminClient, adminToken, clientId, challenge);

        var tokenContent = BuildTokenContent(clientId, code, verifier);
        var tokenResp = await _factory.CreateClient().PostAsync("/oauth/token", tokenContent);
        Assert.Equal(HttpStatusCode.OK, tokenResp.StatusCode);
        var tokens = await tokenResp.Content.ReadFromJsonAsync<OAuthTokenResponse>();
        var originalRefreshToken = tokens!.RefreshToken!;

        var refreshContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = originalRefreshToken,
            ["client_id"] = clientId
        });
        var refreshResp = await _factory.CreateClient().PostAsync("/oauth/token", refreshContent);
        Assert.Equal(HttpStatusCode.OK, refreshResp.StatusCode);
        var refreshed = await refreshResp.Content.ReadFromJsonAsync<OAuthTokenResponse>();
        Assert.NotNull(refreshed);
        Assert.False(string.IsNullOrWhiteSpace(refreshed.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(refreshed.RefreshToken));
        Assert.NotEqual(originalRefreshToken, refreshed.RefreshToken);

        var reuseContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = originalRefreshToken,
            ["client_id"] = clientId
        });
        var reuseResp = await _factory.CreateClient().PostAsync("/oauth/token", reuseContent);
        Assert.Equal(HttpStatusCode.BadRequest, reuseResp.StatusCode);
    }

    [Fact]
    public async Task WellKnown_DiscoveryEndpoint_ReturnsValidDocument()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/.well-known/openid-configuration");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("issuer").GetString()));
        Assert.Contains("/oauth/authorize", body.GetProperty("authorization_endpoint").GetString());
        Assert.Contains("/oauth/token", body.GetProperty("token_endpoint").GetString());
        Assert.Contains("jwks.json", body.GetProperty("jwks_uri").GetString());
    }

    [Fact]
    public async Task WellKnown_Jwks_ReturnsKeySet()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("keys", out _));
    }

    // --- Helpers ---

    private async Task<string> GetAdminTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = "AUTHCENTER"
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuthCenter.Contracts.Responses.Auth.AuthResponse>>();
        return body!.Data!.AccessToken;
    }

    private async Task<(string clientId, string? secret)> CreateOAuthClientAsync(HttpClient client, string clientId)
    {
        var req = new CreateOAuthClientRequest
        {
            ClientId = clientId,
            DisplayName = "Test Client",
            ClientType = (int)OAuthClientType.Confidential,
            RedirectUris = ["https://myapp.com/callback"],
            AllowedScopes = ["openid", "email", "offline_access"],
            GrantTypes = ["authorization_code", "refresh_token"],
            LoginUrl = "https://myapp.com/login",
            RequirePkce = true
        };
        var resp = await client.PostAsJsonAsync("/api/oauth/clients", req);
        resp.EnsureSuccessStatusCode();
        var created = await ReadDataAsync<OAuthClientCreatedResponse>(resp);
        return (created.Client.ClientId, created.ClientSecret);
    }

    private async Task<(string clientId, string? secret)> CreatePublicOAuthClientAsync(HttpClient client, string clientId)
    {
        var req = new CreateOAuthClientRequest
        {
            ClientId = clientId,
            DisplayName = "Public Test Client",
            ClientType = (int)OAuthClientType.Public,
            RedirectUris = ["https://myapp.com/callback"],
            AllowedScopes = ["openid", "email", "offline_access"],
            GrantTypes = ["authorization_code", "refresh_token"],
            LoginUrl = "https://myapp.com/login",
            RequirePkce = true
        };
        var resp = await client.PostAsJsonAsync("/api/oauth/clients", req);
        resp.EnsureSuccessStatusCode();
        var created = await ReadDataAsync<OAuthClientCreatedResponse>(resp);
        return (created.Client.ClientId, created.ClientSecret);
    }

    private async Task<string> GetAuthorizationCodeAsync(HttpClient adminClient, string adminToken, string clientId, string challenge)
    {
        var noRedirect = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var authorizeUrl = $"/oauth/authorize?response_type=code&client_id={clientId}" +
                           $"&redirect_uri=https://myapp.com/callback" +
                           $"&scope=openid+email+offline_access" +
                           $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                           $"&code_challenge_method=S256";

        var authorizeResp = await noRedirect.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, authorizeResp.StatusCode);

        var interactionId = ExtractQueryParam(authorizeResp.Headers.Location!.ToString(), "interaction_id");

        var completeClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        completeClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var completeResp = await completeClient.PostAsJsonAsync("/oauth/authorize/complete", new CompleteAuthorizationRequest
        {
            InteractionId = interactionId,
            Consent = true
        });
        Assert.Equal(HttpStatusCode.Redirect, completeResp.StatusCode);

        return Uri.UnescapeDataString(ExtractQueryParam(completeResp.Headers.Location!.ToString(), "code"));
    }

    private static FormUrlEncodedContent BuildTokenContent(string clientId, string code, string verifier)
        => new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://myapp.com/callback",
            ["client_id"] = clientId,
            ["code_verifier"] = verifier
        });

    private static (string verifier, string challenge) GeneratePkce()
    {
        var verifierBytes = new byte[32];
        RandomNumberGenerator.Fill(verifierBytes);
        var verifier = Convert.ToBase64String(verifierBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (verifier, challenge);
    }

    private static string ExtractQueryParam(string url, string param)
    {
        var uri = new Uri(url.StartsWith("http") ? url : $"https://dummy{url}");
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return query[param] ?? string.Empty;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        Assert.NotNull(body);
        Assert.True(body.Success, $"Expected success but got: {body.ErrorCode} — {body.Message}");
        Assert.NotNull(body.Data);
        return body.Data;
    }
}
