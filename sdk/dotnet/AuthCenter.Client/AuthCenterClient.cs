using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuthCenter.Client;

public sealed class AuthCenterClient(HttpClient httpClient, AuthCenterClientOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds the authorization request. <paramref name="resources"/> are RFC 8707 resource
    /// indicators: the APIs whose scopes are requested.
    /// </summary>
    public Uri BuildAuthorizationUri(Uri redirectUri, string state, string nonce, PkcePair pkce, IEnumerable<string>? scopes = null, IEnumerable<string>? resources = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", options.ClientId),
            new("redirect_uri", redirectUri.AbsoluteUri),
            new("scope", string.Join(' ', scopes ?? options.Scopes)),
            new("state", state),
            new("nonce", nonce),
            new("code_challenge", pkce.Challenge),
            new("code_challenge_method", "S256")
        };
        foreach (var resource in resources ?? [])
            query.Add(new("resource", resource));
        return Endpoint("oauth/authorize?" + string.Join('&', query.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}")));
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

    /// <summary>A machine token for one API (RFC 8707): its audience is <paramref name="resource"/>.</summary>
    public Task<OAuthTokenSet> ClientCredentialsForResourceAsync(string resource, IEnumerable<string>? scopes = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        var values = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = options.ClientId,
            ["resource"] = resource
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

    /// <summary>Refreshes a grant that covers several APIs, asking for a token for one of them.</summary>
    public Task<OAuthTokenSet> RefreshForResourceAsync(string refreshToken, string resource, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        return RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = options.ClientId,
            ["refresh_token"] = refreshToken,
            ["resource"] = resource
        }, ct);
    }

    /// <summary>
    /// RFC 8693 token exchange: an API trades the user's access token it received for a token to
    /// another API, acting on the user's behalf. Requires a confidential client with that grant.
    /// </summary>
    public Task<OAuthTokenSet> ExchangeTokenAsync(string subjectToken, string resource, IEnumerable<string>? scopes = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        var values = new Dictionary<string, string>
        {
            ["grant_type"] = TokenExchangeGrantType,
            ["client_id"] = options.ClientId,
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = AccessTokenType,
            ["resource"] = resource
        };
        var requested = scopes?.Where(scope => !string.IsNullOrWhiteSpace(scope)).Distinct(StringComparer.Ordinal).ToArray();
        if (requested is { Length: > 0 })
            values["scope"] = string.Join(' ', requested);
        return RequestTokenAsync(values, ct);
    }

    /// <summary>RFC 7662: asks AuthCenter whether a token is still active and what it grants.</summary>
    public async Task<AuthCenterIntrospectionResult> IntrospectAsync(string token, string? tokenTypeHint = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var values = new Dictionary<string, string> { ["token"] = token, ["client_id"] = options.ClientId };
        if (!string.IsNullOrWhiteSpace(tokenTypeHint))
            values["token_type_hint"] = tokenTypeHint;
        using var request = CreateFormRequest("oauth/introspect", values);
        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await response.Content.ReadAsStringAsync(ct), null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<AuthCenterIntrospectionResult>(Json, ct)
            ?? throw new InvalidOperationException("AuthCenter returned an empty introspection response.");
    }

    public async Task RevokeAsync(string token, CancellationToken ct = default)
    {
        using var request = CreateFormRequest("oauth/revoke", new Dictionary<string, string> { ["token"] = token, ["client_id"] = options.ClientId });
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    public static PkcePair CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        return new PkcePair(verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private async Task<OAuthTokenSet> RequestTokenAsync(Dictionary<string, string> values, CancellationToken ct)
    {
        using var request = CreateFormRequest("oauth/token", values);
        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await response.Content.ReadAsStringAsync(ct), null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<OAuthTokenSet>(Json, ct)
            ?? throw new InvalidOperationException("AuthCenter returned an empty token response.");
    }

    // Relative to the authority so a path base (https://host/identity) is kept.
    private Uri Endpoint(string relativePath) =>
        new(new Uri(options.Authority.AbsoluteUri.TrimEnd('/') + "/"), relativePath);

    private HttpRequestMessage CreateFormRequest(string path, Dictionary<string, string> values)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(path)) { Content = new FormUrlEncodedContent(values) };
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
