using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuthCenter.Client;

public sealed class AuthCenterClient(HttpClient httpClient, AuthCenterClientOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DiscoveryLifetime = TimeSpan.FromHours(1);

    // Discovery is public metadata of an authority, shared by every client instance of the process.
    private static readonly ConcurrentDictionary<string, (Task<AuthCenterDiscoveryDocument> Document, DateTimeOffset ExpiresAt)> Discovery = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads AuthCenter's OpenID Connect discovery document (cached for an hour). Its issuer must be
    /// the configured authority and every endpoint must use HTTPS; the token, revocation,
    /// introspection, UserInfo and logout calls of this client use its endpoints.
    /// </summary>
    public async Task<AuthCenterDiscoveryDocument> GetDiscoveryDocumentAsync(CancellationToken ct = default)
    {
        var key = options.Authority.AbsoluteUri.TrimEnd('/');
        var entry = Discovery.GetOrAdd(key, _ => (LoadDiscoveryAsync(), DateTimeOffset.UtcNow.Add(DiscoveryLifetime)));
        if (entry.ExpiresAt <= DateTimeOffset.UtcNow || entry.Document.IsFaulted || entry.Document.IsCanceled)
        {
            var fresh = (LoadDiscoveryAsync(), DateTimeOffset.UtcNow.Add(DiscoveryLifetime));
            entry = Discovery.TryUpdate(key, fresh, entry) ? fresh : Discovery[key];
        }
        try
        {
            return await entry.Document.WaitAsync(ct);
        }
        catch when (entry.Document.IsFaulted)
        {
            Discovery.TryRemove(new KeyValuePair<string, (Task<AuthCenterDiscoveryDocument>, DateTimeOffset)>(key, entry));
            throw;
        }
    }

    private async Task<AuthCenterDiscoveryDocument> LoadDiscoveryAsync()
    {
        using var response = await httpClient.GetAsync(Endpoint(".well-known/openid-configuration"), CancellationToken.None);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"AuthCenter discovery failed with status {(int)response.StatusCode}.", null, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<AuthCenterDiscoveryDocument>(Json)
            ?? throw new InvalidOperationException("AuthCenter returned an empty discovery document.");
        if (!string.Equals(document.Issuer.TrimEnd('/'), options.Authority.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal))
            throw new InvalidOperationException("The AuthCenter discovery document belongs to another issuer.");
        foreach (var endpoint in new[] { document.AuthorizationEndpoint, document.TokenEndpoint, document.JwksUri })
            if (!IsHttps(endpoint)) throw new InvalidOperationException("The AuthCenter discovery document has an endpoint that is not HTTPS.");
        foreach (var endpoint in new[] { document.RevocationEndpoint, document.IntrospectionEndpoint, document.UserInfoEndpoint, document.EndSessionEndpoint })
            if (endpoint is not null && !IsHttps(endpoint)) throw new InvalidOperationException("The AuthCenter discovery document has an endpoint that is not HTTPS.");
        return document;
    }

    /// <summary>
    /// Builds the authorization request against the discovered authorization endpoint; same
    /// parameters as <see cref="BuildAuthorizationUri"/>.
    /// </summary>
    public async Task<Uri> BuildAuthorizationUriAsync(
        Uri redirectUri,
        string state,
        string nonce,
        PkcePair pkce,
        IEnumerable<string>? scopes = null,
        IEnumerable<string>? resources = null,
        string? identityProvider = null,
        string? domainHint = null,
        CancellationToken ct = default)
    {
        var conventional = BuildAuthorizationUri(redirectUri, state, nonce, pkce, scopes, resources, identityProvider, domainHint);
        var discovery = await GetDiscoveryDocumentAsync(ct);
        return new UriBuilder(discovery.AuthorizationEndpoint) { Query = conventional.Query.TrimStart('?') }.Uri;
    }

    /// <summary>OpenID Connect UserInfo claims for an access token issued with <c>openid</c>.</summary>
    public async Task<JsonElement> GetUserInfoAsync(string accessToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        var discovery = await GetDiscoveryDocumentAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, discovery.UserInfoEndpoint ?? Endpoint("oauth/userinfo").AbsoluteUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await response.Content.ReadAsStringAsync(ct), null, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// RP-initiated logout URL (<c>end_session_endpoint</c>): ends the AuthCenter single sign-on
    /// session and returns to <paramref name="postLogoutRedirectUri"/>, which must be registered.
    /// </summary>
    public async Task<Uri> BuildEndSessionUriAsync(string? idTokenHint = null, Uri? postLogoutRedirectUri = null, string? state = null, CancellationToken ct = default)
    {
        var discovery = await GetDiscoveryDocumentAsync(ct);
        var query = new List<KeyValuePair<string, string>> { new("client_id", options.ClientId) };
        if (!string.IsNullOrWhiteSpace(idTokenHint)) query.Add(new("id_token_hint", idTokenHint));
        if (postLogoutRedirectUri is not null) query.Add(new("post_logout_redirect_uri", postLogoutRedirectUri.AbsoluteUri));
        if (!string.IsNullOrWhiteSpace(state)) query.Add(new("state", state));
        return new UriBuilder(discovery.EndSessionEndpoint ?? Endpoint("oauth/logout").AbsoluteUri)
        {
            Query = string.Join('&', query.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"))
        }.Uri;
    }

    /// <summary>
    /// Builds the authorization request. <paramref name="resources"/> are RFC 8707 resource
    /// indicators: the APIs whose scopes are requested. <c>identityProvider</c> (a federation
    /// provider ID, sent as <c>idp</c>) makes AuthCenter's hosted login go straight to that
    /// provider; <c>domainHint</c> (<c>domain_hint</c>) lets it find the user's organization.
    /// </summary>
    public Uri BuildAuthorizationUri(
        Uri redirectUri,
        string state,
        string nonce,
        PkcePair pkce,
        IEnumerable<string>? scopes = null,
        IEnumerable<string>? resources = null,
        string? identityProvider = null,
        string? domainHint = null)
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
        if (identityProvider is not null)
            query.Add(new(AuthCenterChallengeParameters.IdentityProviderParameter,
                AuthCenterChallengeParameters.IdentityProvider(identityProvider) ?? throw new ArgumentException("The identity provider must be an AuthCenter federation provider ID.", nameof(identityProvider))));
        if (domainHint is not null)
            query.Add(new(AuthCenterChallengeParameters.DomainHintParameter,
                AuthCenterChallengeParameters.DomainHint(domainHint) ?? throw new ArgumentException("The domain hint must be a domain name.", nameof(domainHint))));
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
        var discovery = await GetDiscoveryDocumentAsync(ct);
        using var request = CreateFormRequest(discovery.IntrospectionEndpoint ?? Endpoint("oauth/introspect").AbsoluteUri, values);
        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await response.Content.ReadAsStringAsync(ct), null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<AuthCenterIntrospectionResult>(Json, ct)
            ?? throw new InvalidOperationException("AuthCenter returned an empty introspection response.");
    }

    public async Task RevokeAsync(string token, CancellationToken ct = default)
    {
        var discovery = await GetDiscoveryDocumentAsync(ct);
        using var request = CreateFormRequest(discovery.RevocationEndpoint ?? Endpoint("oauth/revoke").AbsoluteUri, new Dictionary<string, string> { ["token"] = token, ["client_id"] = options.ClientId });
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
        var discovery = await GetDiscoveryDocumentAsync(ct);
        using var request = CreateFormRequest(discovery.TokenEndpoint, values);
        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await response.Content.ReadAsStringAsync(ct), null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<OAuthTokenSet>(Json, ct)
            ?? throw new InvalidOperationException("AuthCenter returned an empty token response.");
    }

    // Relative to the authority so a path base (https://host/identity) is kept.
    private Uri Endpoint(string relativePath) =>
        new(new Uri(options.Authority.AbsoluteUri.TrimEnd('/') + "/"), relativePath);

    private HttpRequestMessage CreateFormRequest(string endpoint, Dictionary<string, string> values)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(values) };
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

    private static bool IsHttps(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
