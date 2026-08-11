using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;

namespace AuthCenter.IntegrationTests;

public sealed class PasskeyTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public PasskeyTests(AuthCenterWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RegistrationOptions_RequireAuthentication_AndUseExplicitRelyingParty()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/auth/passkeys/registration/options", null)).StatusCode);

        using var client = await CreateAdminClientAsync();
        var options = await ReadDataAsync<PasskeyOptionsResponse>(await client.PostAsync("/api/auth/passkeys/registration/options", null));
        Assert.Equal("localhost", options.PublicKey.GetProperty("rp").GetProperty("id").GetString());
        Assert.Equal("required", options.PublicKey.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());

        var credentials = await ReadDataAsync<List<PasskeyCredentialDto>>(await client.GetAsync("/api/auth/passkeys"));
        Assert.Empty(credentials);
    }

    [Fact]
    public async Task LoginCeremony_IsSingleUse_EvenWhenAssertionIsInvalid()
    {
        using var client = _factory.CreateClient();
        var options = await ReadDataAsync<PasskeyOptionsResponse>(await client.PostAsJsonAsync("/api/auth/passkeys/login/options",
            new BeginPasskeyLoginRequest
            {
                ApplicationCode = DomainConstants.SystemCodes.AuthCenter,
                Email = AuthCenterWebApplicationFactory.AdminEmail
            }));
        Assert.False(string.IsNullOrWhiteSpace(options.InteractionId));
        Assert.True(options.PublicKey.TryGetProperty("challenge", out _));

        var invalid = new CompletePasskeyLoginRequest { InteractionId = options.InteractionId!, CredentialJson = "{}" };
        var first = await client.PostAsJsonAsync("/api/auth/passkeys/login/complete", invalid);
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("INVALID_PASSKEY_ASSERTION", firstBody?.ErrorCode);

        var replay = await client.PostAsJsonAsync("/api/auth/passkeys/login/complete", invalid);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var replayBody = await replay.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("PASSKEY_CEREMONY_EXPIRED", replayBody?.ErrorCode);
    }

    [Fact]
    public async Task ManagementEndpoints_RejectInvalidCredentialData()
    {
        using var client = await CreateAdminClientAsync();
        var withoutProof = await client.PostAsJsonAsync("/api/auth/passkeys/registration/complete",
            new RegisterPasskeyRequest { Name = "Laptop", CredentialJson = "{}" });
        Assert.Equal(HttpStatusCode.Forbidden, withoutProof.StatusCode);

        var enrollmentProof = await CreateProofAsync(client, "factor.enroll");
        client.DefaultRequestHeaders.Add("X-AuthCenter-Reauthentication", enrollmentProof);
        var registration = await client.PostAsJsonAsync("/api/auth/passkeys/registration/complete",
            new RegisterPasskeyRequest { Name = "Laptop", CredentialJson = "{}" });
        Assert.Equal(HttpStatusCode.BadRequest, registration.StatusCode);
        client.DefaultRequestHeaders.Remove("X-AuthCenter-Reauthentication");

        client.DefaultRequestHeaders.Add("X-AuthCenter-Reauthentication", await CreateProofAsync(client, "passkey.manage"));
        var rename = await client.PutAsJsonAsync("/api/auth/passkeys/not-base64!", new RenamePasskeyRequest { Name = "Laptop" });
        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/auth/passkeys/not-base64!")).StatusCode);
    }

    private static async Task<string> CreateProofAsync(HttpClient client, string purpose)
    {
        var response = await ReadDataAsync<ReauthenticationProofResponse>(await client.PostAsJsonAsync(
            "/api/auth/reauth/password",
            new PasswordReauthenticationRequest
            {
                Purpose = purpose,
                Password = AuthCenterWebApplicationFactory.AdminPassword
            }));
        Assert.Equal("Password", response.AssuranceLevel);
        return response.ProofToken;
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

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        Assert.NotNull(body);
        Assert.NotNull(body.Data);
        return body.Data;
    }
}
