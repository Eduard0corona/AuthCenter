using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.OAuth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AuthCenter.IntegrationTests;

[Trait("Category", "Conformance")]
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
            ApplicationSystemId = await GetApplicationSystemIdAsync(),
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
        Assert.DoesNotContain(created.ClientSecret!, await getResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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
            IsActive = false
        };

        var updateWithoutProof = await client.PutAsJsonAsync($"/api/oauth/clients/{clientId}", updateRequest);
        Assert.Equal(HttpStatusCode.Forbidden, updateWithoutProof.StatusCode);
        await client.AddReauthenticationProofAsync(
            AuthCenterWebApplicationFactory.AdminPassword,
            "admin.oauth-client.deactivate");
        var updateResponse = await client.PutAsJsonAsync($"/api/oauth/clients/{clientId}", updateRequest);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await ReadDataAsync<OAuthClientResponse>(updateResponse);
        Assert.Equal("Updated Name", updated.DisplayName);
        Assert.Equal(1800, updated.AccessTokenLifetimeSeconds);
        Assert.False(updated.IsActive);

        var withoutProof = await client.DeleteAsync($"/api/oauth/clients/{clientId}");
        Assert.Equal(HttpStatusCode.Forbidden, withoutProof.StatusCode);
        Assert.Equal("REAUTHENTICATION_REQUIRED", (await withoutProof.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);

        await client.AddReauthenticationProofAsync(
            AuthCenterWebApplicationFactory.AdminPassword,
            "admin.oauth-client.deactivate");
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

        var withoutProof = await client.PostAsync($"/api/oauth/clients/{clientId}/rotate-secret", null);
        Assert.Equal(HttpStatusCode.Forbidden, withoutProof.StatusCode);

        await client.AddReauthenticationProofAsync(
            AuthCenterWebApplicationFactory.AdminPassword,
            "admin.oauth-client.rotate-secret");
        var rotateResponse = await client.PostAsync($"/api/oauth/clients/{clientId}/rotate-secret", null);
        Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);
        var rotated = await ReadDataAsync<RotateClientSecretResponse>(rotateResponse);

        Assert.False(string.IsNullOrWhiteSpace(rotated.ClientSecret));
        Assert.NotEqual(originalSecret, rotated.ClientSecret);

        var reusedProof = await client.PostAsync($"/api/oauth/clients/{clientId}/rotate-secret", null);
        Assert.Equal(HttpStatusCode.Forbidden, reusedProof.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var audits = await db.AuditLogs
            .Where(audit => audit.EntityId == clientId)
            .Select(audit => new { audit.Action, audit.MetadataJson })
            .ToListAsync();
        Assert.Contains(audits, audit => audit.Action == "OAUTH_CLIENT_SECRET_ROTATED");
        Assert.Contains(audits, audit => audit.Action == "OAUTH_CLIENT_SECRET_ROTATION_REJECTED");
        Assert.DoesNotContain(audits, audit =>
            (audit.MetadataJson ?? string.Empty).Contains(rotated.ClientSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OAuthClient_List_Filters_By_Search_Application_Type_And_Status()
    {
        using var client = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var clientId = $"filter-{Guid.NewGuid():N}"[..20];
        var (createdClientId, _) = await CreateOAuthClientAsync(client, clientId);
        var applicationId = await GetApplicationSystemIdAsync();

        var response = await client.GetAsync(
            $"/api/oauth/clients?page=1&pageSize=20&search={Uri.EscapeDataString(clientId)}&applicationSystemId={applicationId}&clientType=0&isActive=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await ReadDataAsync<PagedResult<OAuthClientResponse>>(response);
        Assert.Single(page.Items);
        Assert.Equal(createdClientId, page.Items[0].ClientId);
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
                           $"&state=xyz123" +
                           $"&nonce=nonce-xyz123";

        var authorizeResponse = await noRedirectClient.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);

        var loginUrl = authorizeResponse.Headers.Location!.ToString();
        Assert.Contains("interaction_id=", loginUrl);
        var interactionId = ExtractQueryParam(loginUrl, "interaction_id");
        Assert.False(string.IsNullOrWhiteSpace(interactionId));

        var interactionResponse = await adminClient.GetAsync($"/oauth/interactions/{interactionId}");
        Assert.Equal(HttpStatusCode.OK, interactionResponse.StatusCode);
        var interaction = await ReadDataAsync<OAuthInteractionResponse>(interactionResponse);
        Assert.Equal(clientId, interaction.ClientId);
        Assert.Equal("AUTHCENTER", interaction.ApplicationCode);
        Assert.True(interaction.RequiresConsent);
        Assert.Contains("offline_access", interaction.Scopes);
        // The consent screen describes each scope in the user's words, in the requested order.
        Assert.Equal(interaction.Scopes, interaction.ScopeDescriptions.Select(item => item.Scope).ToList());
        Assert.Equal("Ver tu dirección de correo", interaction.ScopeDescriptions.Single(item => item.Scope == "email").Description);
        Assert.Equal("Mantener el acceso aunque no estés usando la aplicación", interaction.ScopeDescriptions.Single(item => item.Scope == "offline_access").Description);

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

        var accessJwt = new JwtSecurityTokenHandler().ReadJwtToken(tokenBody.AccessToken);
        Assert.Contains(accessJwt.Claims, claim =>
            claim.Type == DomainConstants.Claims.Applications && claim.Value == "AUTHCENTER");
        Assert.Contains(accessJwt.Claims, claim => claim.Type == DomainConstants.Claims.Role);
        Assert.Equal(DomainConstants.Claims.AccessTokenType, accessJwt.Header.Typ);
        Assert.Contains(accessJwt.Claims, claim => claim.Type == DomainConstants.Claims.Permissions);
        Assert.Contains(accessJwt.Claims, claim =>
            claim.Type == JwtRegisteredClaimNames.Email && claim.Value == AuthCenterWebApplicationFactory.AdminEmail);
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
            ApplicationSystemId = await GetApplicationSystemIdAsync(),
            ClientId = clientId,
            DisplayName = "Machine Client",
            ClientType = (int)OAuthClientType.Confidential,
            RedirectUris = [],
            AllowedScopes = ["email"],
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
    public async Task OAuthAccessToken_IsRs256AndValidatesAgainstPublishedJwks()
    {
        using var adminClient = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var clientId = $"rsa-{Guid.NewGuid():N}"[..20];
        var createResponse = await adminClient.PostAsJsonAsync("/api/oauth/clients", new CreateOAuthClientRequest
        {
            ApplicationSystemId = await GetApplicationSystemIdAsync(),
            ClientId = clientId,
            DisplayName = "RSA Validation Client",
            ClientType = (int)OAuthClientType.Confidential,
            RedirectUris = [],
            AllowedScopes = ["email"],
            GrantTypes = ["client_credentials"],
            LoginUrl = "https://internal.service/",
            RequirePkce = false
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await ReadDataAsync<OAuthClientCreatedResponse>(createResponse);

        using var tokenClient = _factory.CreateClient();
        var tokenResponse = await tokenClient.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = created.ClientSecret!,
            ["scope"] = "email"
        }));
        tokenResponse.EnsureSuccessStatusCode();
        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<OAuthTokenResponse>();
        Assert.NotNull(tokenBody);

        var handler = new JwtSecurityTokenHandler();
        handler.InboundClaimTypeMap.Clear();
        var jwt = handler.ReadJwtToken(tokenBody.AccessToken);
        Assert.Equal(SecurityAlgorithms.RsaSha256, jwt.Header.Alg);
        Assert.False(string.IsNullOrWhiteSpace(jwt.Header.Kid));

        var jwks = await tokenClient.GetFromJsonAsync<JsonElement>("/.well-known/jwks.json");
        var jwk = jwks.GetProperty("keys").EnumerateArray().Single(key =>
            key.GetProperty("kid").GetString() == jwt.Header.Kid);

        // Built from parameters, not from a disposable RSA: signature providers are cached in the
        // process-wide CryptoProviderFactory.Default by key thumbprint, so a provider holding an RSA
        // disposed here would break every later validation of the same key in this test run.
        var signingKey = new RsaSecurityKey(new RSAParameters
        {
            Modulus = Base64UrlEncoder.DecodeBytes(jwk.GetProperty("n").GetString()!),
            Exponent = Base64UrlEncoder.DecodeBytes(jwk.GetProperty("e").GetString()!)
        })
        { KeyId = jwt.Header.Kid };

        var discovery = await tokenClient.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        var principal = handler.ValidateToken(tokenBody.AccessToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = discovery.GetProperty("issuer").GetString(),
            ValidAudience = clientId,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
        }, out _);

        Assert.Equal(clientId, principal.FindFirst("client_id")?.Value);
        Assert.Equal("email", principal.FindFirst("scope")?.Value);
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

        var replacementAfterReplay = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshed.RefreshToken!,
            ["client_id"] = clientId
        });
        var replacementResp = await _factory.CreateClient().PostAsync("/oauth/token", replacementAfterReplay);
        Assert.Equal(HttpStatusCode.BadRequest, replacementResp.StatusCode);
    }

    [Fact]
    public async Task UserInfo_WithOAuthAccessToken_ReturnsClaimsForGrantedScopes()
    {
        using var adminClient = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var clientId = $"ui-{Guid.NewGuid():N}"[..20];
        await CreatePublicOAuthClientAsync(adminClient, clientId);

        var (verifier, challenge) = GeneratePkce();
        var code = await GetAuthorizationCodeAsync(adminClient, adminToken, clientId, challenge);

        using var tokenClient = _factory.CreateClient();
        var tokenResponse = await tokenClient.PostAsync("/oauth/token", BuildTokenContent(clientId, code, verifier));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<OAuthTokenResponse>();

        using var userInfoClient = _factory.CreateClient();
        userInfoClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var userInfoResponse = await userInfoClient.GetAsync("/oauth/userinfo");
        Assert.Equal(HttpStatusCode.OK, userInfoResponse.StatusCode);

        // OIDC userinfo returns the claims document directly, not the ApiResponse envelope.
        var userInfo = await userInfoResponse.Content.ReadFromJsonAsync<OAuthUserInfoResponse>();
        Assert.NotNull(userInfo);
        Assert.False(string.IsNullOrWhiteSpace(userInfo.Sub));
        Assert.Equal(AuthCenterWebApplicationFactory.AdminEmail, userInfo.Email);
        // The client was authorized for "openid email offline_access" but not "profile".
        Assert.Null(userInfo.Name);
    }

    [Fact]
    public async Task UserInfo_WithFirstPartyLoginToken_IsRejected()
    {
        using var client = _factory.CreateClient();
        var loginToken = await GetAdminTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginToken);

        var response = await client.GetAsync("/oauth/userinfo");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UserInfo_WithClientCredentialsToken_IsRejected()
    {
        using var adminClient = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var clientId = $"uicc-{Guid.NewGuid():N}"[..20];
        var createResponse = await adminClient.PostAsJsonAsync("/api/oauth/clients", new CreateOAuthClientRequest
        {
            ApplicationSystemId = await GetApplicationSystemIdAsync(),
            ClientId = clientId,
            DisplayName = "Machine Client",
            ClientType = (int)OAuthClientType.Confidential,
            RedirectUris = [],
            AllowedScopes = ["email"],
            GrantTypes = ["client_credentials"],
            LoginUrl = "https://internal.service/",
            RequirePkce = false
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await ReadDataAsync<OAuthClientCreatedResponse>(createResponse);

        using var tokenClient = _factory.CreateClient();
        var tokenResponse = await tokenClient.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = created.ClientSecret!,
            ["scope"] = "email"
        }));
        tokenResponse.EnsureSuccessStatusCode();
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<OAuthTokenResponse>();

        using var userInfoClient = _factory.CreateClient();
        userInfoClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        // There is no end user behind a client_credentials token, so there is nothing to return.
        var userInfoResponse = await userInfoClient.GetAsync("/oauth/userinfo");
        Assert.Equal(HttpStatusCode.Unauthorized, userInfoResponse.StatusCode);
    }

    [Fact]
    public async Task OAuthClient_Create_RejectsUnknownApplication()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetAdminTokenAsync(client));

        var response = await client.PostAsJsonAsync("/api/oauth/clients", new CreateOAuthClientRequest
        {
            ApplicationSystemId = Guid.NewGuid(),
            ClientId = $"missing-{Guid.NewGuid():N}"[..20],
            DisplayName = "Missing application",
            ClientType = (int)OAuthClientType.Public,
            RedirectUris = ["https://myapp.com/callback"],
            AllowedScopes = ["openid"],
            GrantTypes = ["authorization_code"],
            LoginUrl = "https://myapp.com/login",
            RequirePkce = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("APPLICATION_NOT_FOUND", body?.ErrorCode);
    }

    [Fact]
    public async Task Authorization_InvalidScope_RedirectsToRegisteredClientWithStateAndIssuer()
    {
        using var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetAdminTokenAsync(adminClient));
        var clientId = $"scope-{Guid.NewGuid():N}"[..20];
        await CreatePublicOAuthClientAsync(adminClient, clientId);
        var (_, challenge) = GeneratePkce();

        using var browser = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await browser.GetAsync(
            $"/oauth/authorize?response_type=code&client_id={clientId}" +
            "&redirect_uri=https://myapp.com/callback" +
            "&scope=openid+unknown" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256" +
            "&state=state-123&nonce=nonce-123");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://myapp.com/callback", location, StringComparison.Ordinal);
        Assert.Equal("invalid_scope", ExtractQueryParam(location, "error"));
        Assert.Equal("state-123", ExtractQueryParam(location, "state"));
        Assert.False(string.IsNullOrWhiteSpace(ExtractQueryParam(location, "iss")));
    }

    [Fact]
    public async Task Authorization_UserWithoutApplicationAccess_IsDenied()
    {
        using var adminClient = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var applicationId = await CreateApplicationSystemAsync();
        var clientId = $"isolated-{Guid.NewGuid():N}"[..20];
        await CreatePublicOAuthClientAsync(adminClient, clientId, applicationSystemId: applicationId);
        var (_, challenge) = GeneratePkce();

        using var browser = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var authorize = await browser.GetAsync(
            $"/oauth/authorize?response_type=code&client_id={clientId}" +
            "&redirect_uri=https://myapp.com/callback&scope=openid+email+offline_access" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256" +
            "&state=isolated-state&nonce=isolated-nonce");
        var interactionId = ExtractQueryParam(authorize.Headers.Location!.ToString(), "interaction_id");

        using var completeClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        completeClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var complete = await completeClient.PostAsJsonAsync("/oauth/authorize/complete", new CompleteAuthorizationRequest
        {
            InteractionId = interactionId,
            Consent = true
        });

        Assert.Equal(HttpStatusCode.Redirect, complete.StatusCode);
        Assert.Equal("access_denied", ExtractQueryParam(complete.Headers.Location!.ToString(), "error"));
    }

    [Fact]
    public async Task ClientCredentials_AcceptsHttpBasicAuthentication()
    {
        using var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetAdminTokenAsync(adminClient));
        var clientId = $"basic-{Guid.NewGuid():N}"[..20];
        var (_, secret) = await CreateMachineClientAsync(adminClient, clientId);

        using var tokenClient = _factory.CreateClient();
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}"));
        tokenClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        var response = await tokenClient.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "email"
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>();
        Assert.False(string.IsNullOrWhiteSpace(token?.AccessToken));
    }

    [Fact]
    public async Task RevocationEndpoint_RevokesRefreshTokenFamily()
    {
        using var adminClient = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var clientId = $"revoke-{Guid.NewGuid():N}"[..20];
        await CreatePublicOAuthClientAsync(adminClient, clientId);
        var (verifier, challenge) = GeneratePkce();
        var code = await GetAuthorizationCodeAsync(adminClient, adminToken, clientId, challenge);
        var issued = await (await _factory.CreateClient().PostAsync("/oauth/token", BuildTokenContent(clientId, code, verifier)))
            .Content.ReadFromJsonAsync<OAuthTokenResponse>();

        var revoke = await _factory.CreateClient().PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = issued!.RefreshToken!,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = clientId
        }));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var refresh = await _factory.CreateClient().PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = issued.RefreshToken!,
            ["client_id"] = clientId
        }));
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
    }

    [Fact]
    public async Task AutoConsent_ClientCanCompleteWithoutExplicitConsent()
    {
        using var adminClient = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var clientId = $"auto-{Guid.NewGuid():N}"[..20];
        await CreatePublicOAuthClientAsync(adminClient, clientId, autoConsent: true);
        var (_, challenge) = GeneratePkce();

        using var browser = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var authorize = await browser.GetAsync(
            $"/oauth/authorize?response_type=code&client_id={clientId}" +
            "&redirect_uri=https://myapp.com/callback&scope=openid+email+offline_access" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256" +
            "&state=auto-state&nonce=auto-nonce");
        var interactionId = ExtractQueryParam(authorize.Headers.Location!.ToString(), "interaction_id");

        using var completeClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        completeClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var complete = await completeClient.PostAsJsonAsync("/oauth/authorize/complete", new CompleteAuthorizationRequest
        {
            InteractionId = interactionId,
            Consent = false
        });

        Assert.Equal(HttpStatusCode.Redirect, complete.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(ExtractQueryParam(complete.Headers.Location!.ToString(), "code")));
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
        Assert.Contains("/oauth/revoke", body.GetProperty("revocation_endpoint").GetString());
        Assert.Contains("jwks.json", body.GetProperty("jwks_uri").GetString());
        Assert.Contains("S256", body.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(value => value.GetString()));
        Assert.True(body.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
    }

    [Theory]
    [InlineData("authorization_endpoint")]
    [InlineData("token_endpoint")]
    [InlineData("revocation_endpoint")]
    [InlineData("userinfo_endpoint")]
    [InlineData("jwks_uri")]
    public async Task WellKnown_DiscoveryEndpoint_PublishesAbsoluteEndpointUrls(string property)
    {
        using var client = _factory.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");

        var endpoint = body.GetProperty(property).GetString();
        Assert.True(
            Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp),
            $"{property} must be an absolute http(s) URL but was '{endpoint}'.");
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

    [Fact]
    public async Task IdToken_AssertsEmailVerified_OnlyAfterTheOwnerConfirmsTheAddress()
    {
        // An application that lets accounts sign in before they confirm their address.
        var code = $"OPT{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var applicationId = await CreateOpenRegistrationApplicationAsync(code);
        using var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetAdminTokenAsync(adminClient));
        var (clientId, _) = await CreatePublicOAuthClientAsync(
            adminClient, $"ver-{Guid.NewGuid():N}"[..20], autoConsent: true, applicationSystemId: applicationId);

        var email = $"verify-{Guid.NewGuid():N}@example.com";
        using var client = _factory.CreateClient();
        var registered = await ReadDataAsync<AuthCenter.Contracts.Responses.Auth.AuthResponse>(
            await client.PostAsJsonAsync("/api/auth/register", new AuthCenter.Contracts.Requests.Auth.RegisterRequest
            {
                FullName = "Verify Later",
                Email = email,
                Password = "Password123",
                ApplicationCode = code
            }));

        Assert.Equal("false", await EmailVerifiedInIdTokenAsync(registered.AccessToken, clientId));

        string confirmationToken;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            confirmationToken = Assert.Single(
                await OutboxMail.ReadAsync(scope.ServiceProvider),
                mail => mail.Kind == "email-confirmation" && mail.ToEmail == email).Secret;
        }
        var confirmed = await client.PostAsJsonAsync("/api/auth/confirm-email", new AuthCenter.Contracts.Requests.Auth.ConfirmEmailRequest
        {
            Email = email,
            Token = confirmationToken
        });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var signedIn = await ReadDataAsync<AuthCenter.Contracts.Responses.Auth.AuthResponse>(
            await client.PostAsJsonAsync("/api/auth/login", new AuthCenter.Contracts.Requests.Auth.LoginRequest
            {
                Email = email,
                Password = "Password123",
                ApplicationCode = code
            }));
        Assert.Equal("true", await EmailVerifiedInIdTokenAsync(signedIn.AccessToken, clientId));
    }

    // --- Helpers ---

    private async Task<string?> EmailVerifiedInIdTokenAsync(string userToken, string clientId)
    {
        var (verifier, challenge) = GeneratePkce();
        using var unused = _factory.CreateClient();
        var authorizationCode = await GetAuthorizationCodeAsync(unused, userToken, clientId, challenge);
        using var tokenClient = _factory.CreateClient();
        var response = await tokenClient.PostAsync("/oauth/token", BuildTokenContent(clientId, authorizationCode, verifier));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>();
        var idToken = new JwtSecurityTokenHandler().ReadJwtToken(tokens!.IdToken);
        return idToken.Claims.SingleOrDefault(claim => claim.Type == "email_verified")?.Value;
    }

    private async Task<Guid> CreateOpenRegistrationApplicationAsync(string code)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var now = DateTime.UtcNow;
        var application = new ApplicationSystem
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = code,
            IsActive = true,
            CreatedAt = now,
            RegistrationSettings = new ApplicationRegistrationSettings
            {
                Id = Guid.NewGuid(),
                RegistrationMode = ApplicationRegistrationMode.Open,
                AllowPasswordLogin = true,
                RequireEmailConfirmation = false,
                CreatedAt = now
            }
        };
        db.ApplicationSystems.Add(application);
        await db.SaveChangesAsync();
        return application.Id;
    }

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
            ApplicationSystemId = await GetApplicationSystemIdAsync(),
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

    private async Task<(string clientId, string? secret)> CreatePublicOAuthClientAsync(
        HttpClient client,
        string clientId,
        bool autoConsent = false,
        Guid? applicationSystemId = null)
    {
        var req = new CreateOAuthClientRequest
        {
            ApplicationSystemId = applicationSystemId ?? await GetApplicationSystemIdAsync(),
            ClientId = clientId,
            DisplayName = "Public Test Client",
            ClientType = (int)OAuthClientType.Public,
            RedirectUris = ["https://myapp.com/callback"],
            AllowedScopes = ["openid", "email", "offline_access"],
            GrantTypes = ["authorization_code", "refresh_token"],
            LoginUrl = "https://myapp.com/login",
            RequirePkce = true,
            AutoConsent = autoConsent
        };
        var resp = await client.PostAsJsonAsync("/api/oauth/clients", req);
        resp.EnsureSuccessStatusCode();
        var created = await ReadDataAsync<OAuthClientCreatedResponse>(resp);
        return (created.Client.ClientId, created.ClientSecret);
    }

    private async Task<(string clientId, string? secret)> CreateMachineClientAsync(HttpClient client, string clientId)
    {
        var req = new CreateOAuthClientRequest
        {
            ApplicationSystemId = await GetApplicationSystemIdAsync(),
            ClientId = clientId,
            DisplayName = "Machine Test Client",
            ClientType = (int)OAuthClientType.Confidential,
            RedirectUris = [],
            AllowedScopes = ["email"],
            GrantTypes = ["client_credentials"],
            LoginUrl = "https://myapp.com/login",
            RequirePkce = false
        };
        var resp = await client.PostAsJsonAsync("/api/oauth/clients", req);
        resp.EnsureSuccessStatusCode();
        var created = await ReadDataAsync<OAuthClientCreatedResponse>(resp);
        return (created.Client.ClientId, created.ClientSecret);
    }

    private async Task<Guid> CreateApplicationSystemAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var application = new ApplicationSystem
        {
            Id = Guid.NewGuid(),
            Code = $"ISO{suffix}",
            Name = $"Isolated application {suffix}",
            Description = "Application used to verify OAuth access isolation.",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.ApplicationSystems.Add(application);
        await db.SaveChangesAsync();
        return application.Id;
    }

    private async Task<string> GetAuthorizationCodeAsync(HttpClient adminClient, string adminToken, string clientId, string challenge)
    {
        var noRedirect = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var authorizeUrl = $"/oauth/authorize?response_type=code&client_id={clientId}" +
                           $"&redirect_uri=https://myapp.com/callback" +
                           $"&scope=openid+email+offline_access" +
                           $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                           $"&code_challenge_method=S256" +
                           $"&state={Uri.EscapeDataString(Guid.NewGuid().ToString("N"))}" +
                           $"&nonce={Uri.EscapeDataString(Guid.NewGuid().ToString("N"))}";

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

    private async Task<Guid> GetApplicationSystemIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.ApplicationSystems
            .Where(application => application.Code == "AUTHCENTER")
            .Select(application => application.Id)
            .SingleAsync();
    }

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
