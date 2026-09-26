using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuthCenter.Client;

public sealed class AuthCenterClient(HttpClient httpClient, AuthCenterClientOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Uri BuildAuthorizationUri(Uri redirectUri, string state, string nonce, PkcePair pkce, IEnumerable<string>? scopes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = string.Join(' ', scopes ?? options.Scopes),
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256"
        };
        return new Uri(options.Authority, "/oauth/authorize?" + string.Join('&', query.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}")));
    }

    public Task<OAuthTokenSet> ExchangeCodeAsync(string code, Uri redirectUri, string verifier, CancellationToken ct = default) =>
        RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = options.ClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code_verifier"] = verifier
        }, ct);

    /// <summary>
    /// Requests a machine token. Without explicit scopes no scope parameter is sent and AuthCenter
    /// issues every machine scope the client is allowed; user scopes such as openid never apply.
    /// </summary>
    public Task<OAuthTokenSet> ClientCredentialsAsync(IEnumerable<string>? scopes = null, CancellationToken ct = default)
    {
        var values = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = options.ClientId
        };
        var requested = scopes?.Where(scope => !string.IsNullOrWhiteSpace(scope)).Distinct(StringComparer.Ordinal).ToArray();
        if (requested is { Length: > 0 })
            values["scope"] = string.Join(' ', requested);
        return RequestTokenAsync(values, ct);
    }

    public Task<OAuthTokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = options.ClientId,
            ["refresh_token"] = refreshToken
        }, ct);

    public async Task RevokeAsync(string token, CancellationToken ct = default)
    {
        using var request = CreateFormRequest("/oauth/revoke", new Dictionary<string, string> { ["token"] = token, ["client_id"] = options.ClientId });
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public static PkcePair CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        return new PkcePair(verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private async Task<OAuthTokenSet> RequestTokenAsync(Dictionary<string, string> values, CancellationToken ct)
    {
        using var request = CreateFormRequest("/oauth/token", values);
        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await response.Content.ReadAsStringAsync(ct), null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<OAuthTokenSet>(Json, ct)
            ?? throw new InvalidOperationException("AuthCenter returned an empty token response.");
    }

    private HttpRequestMessage CreateFormRequest(string path, Dictionary<string, string> values)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Authority, path)) { Content = new FormUrlEncodedContent(values) };
        if (!string.IsNullOrEmpty(options.ClientSecret))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(options.ClientId)}:{Uri.EscapeDataString(options.ClientSecret)}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            values.Remove("client_id");
            request.Content = new FormUrlEncodedContent(values);
        }
        return request;
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
