using System.Net.Http.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;

namespace AuthCenter.IntegrationTests;

internal static class ReauthenticationTestExtensions
{
    public static async Task AddReauthenticationProofAsync(this HttpClient client, string password, string purpose)
    {
        client.DefaultRequestHeaders.Remove("X-AuthCenter-Reauthentication");
        var response = await client.PostAsJsonAsync("/api/auth/reauth/password", new PasswordReauthenticationRequest
        {
            Password = password,
            Purpose = purpose
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<ReauthenticationProofResponse>>();
        if (string.IsNullOrWhiteSpace(body?.Data?.ProofToken))
            throw new InvalidOperationException("Reauthentication proof was not returned.");
        client.DefaultRequestHeaders.Add("X-AuthCenter-Reauthentication", body.Data.ProofToken);
    }
}
