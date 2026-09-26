using System.Collections.Concurrent;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace AuthCenter.Infrastructure.Services;

/// <summary>
/// Caches upstream OpenID Connect discovery documents and signing keys per provider. Documents are
/// fetched through the "Federation" HTTP client, so proxies, timeouts and tests apply to them too.
/// </summary>
public sealed class FederationMetadataCache
{
    public const string HttpClientName = "Federation";
    private const int MaximumDocumentBytes = 512 * 1024;
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new(StringComparer.Ordinal);
    private readonly IHttpClientFactory _httpClients;

    public FederationMetadataCache(IHttpClientFactory httpClients) => _httpClients = httpClients;

    public async Task<OpenIdConnectConfiguration?> GetAsync(Guid providerId, string address, bool refresh, CancellationToken ct)
    {
        var manager = _managers.GetOrAdd($"{providerId:N}|{address}", _ => new ConfigurationManager<OpenIdConnectConfiguration>(
            address, new OpenIdConnectConfigurationRetriever(), new FactoryDocumentRetriever(_httpClients))
        {
            // A token signed with an unknown key forces a refresh at most once a minute, so rotated
            // upstream keys are picked up quickly without letting bad tokens flood the provider.
            RefreshInterval = TimeSpan.FromMinutes(1)
        });
        if (refresh)
            manager.RequestRefresh();
        try
        {
            return await manager.GetConfigurationAsync(ct);
        }
        catch (Exception exception) when (IsRetrievalFailure(exception))
        {
            return null;
        }
    }

    /// <summary>Reads the discovery document and keys now, bypassing the cache (connection tests).</summary>
    public async Task<OpenIdConnectConfiguration?> FetchAsync(string address, CancellationToken ct)
    {
        try
        {
            return await OpenIdConnectConfigurationRetriever.GetAsync(address, new FactoryDocumentRetriever(_httpClients), ct);
        }
        catch (Exception exception) when (IsRetrievalFailure(exception))
        {
            return null;
        }
    }

    private static bool IsRetrievalFailure(Exception exception) =>
        exception is IOException or InvalidOperationException or HttpRequestException or ArgumentException or System.Text.Json.JsonException;

    private sealed class FactoryDocumentRetriever : IDocumentRetriever
    {
        private readonly IHttpClientFactory _httpClients;
        public FactoryDocumentRetriever(IHttpClientFactory httpClients) => _httpClients = httpClients;

        public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Federation metadata must be fetched over HTTPS.", nameof(address));
            using var response = await _httpClients.CreateClient(HttpClientName).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode)
                throw new IOException($"Metadata request failed with status {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > MaximumDocumentBytes)
                throw new IOException("Metadata document is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancel);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancel)) > 0)
            {
                if (buffer.Length + read > MaximumDocumentBytes)
                    throw new IOException("Metadata document is too large.");
                buffer.Write(chunk, 0, read);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}
