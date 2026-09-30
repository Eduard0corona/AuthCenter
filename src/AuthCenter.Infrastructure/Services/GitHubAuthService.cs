using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services;

public class GitHubAuthService : IGitHubAuthService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GitHubAuthSettings _settings;
    private readonly ILogger<GitHubAuthService> _logger;

    public GitHubAuthService(
        IHttpClientFactory httpClientFactory,
        IOptions<GitHubAuthSettings> settings,
        ILogger<GitHubAuthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<ExternalTokenPayload?> GetUserFromAccessTokenAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ClientId))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient("GitHub");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var userResponse = await client.GetAsync("user", ct);
            if (!userResponse.IsSuccessStatusCode)
                return null;

            var user = await userResponse.Content.ReadFromJsonAsync<GitHubUserResponse>(ct);
            if (user is null)
                return null;

            var email = user.Email;
            if (string.IsNullOrWhiteSpace(email))
            {
                var emailsResponse = await client.GetAsync("user/emails", ct);
                if (emailsResponse.IsSuccessStatusCode)
                {
                    var emails = await emailsResponse.Content.ReadFromJsonAsync<List<GitHubEmailResponse>>(ct);
                    email = emails?
                        .Where(e => e.Primary && e.Verified)
                        .Select(e => e.Email)
                        .FirstOrDefault();
                }
            }

            return new ExternalTokenPayload
            {
                Subject = user.Id.ToString(),
                Email = email ?? string.Empty,
                Name = user.Name ?? user.Login,
                PictureUrl = user.AvatarUrl,
                // GitHub only lets a verified address be the public one, and the fallback above
                // takes the primary verified address.
                EmailVerified = !string.IsNullOrWhiteSpace(email)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get GitHub user from access token");
            return null;
        }
    }

    private sealed class GitHubUserResponse
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("login")]
        public string Login { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("avatar_url")]
        public string? AvatarUrl { get; init; }

        [JsonPropertyName("email")]
        public string? Email { get; init; }
    }

    private sealed class GitHubEmailResponse
    {
        [JsonPropertyName("email")]
        public string Email { get; init; } = string.Empty;

        [JsonPropertyName("primary")]
        public bool Primary { get; init; }

        [JsonPropertyName("verified")]
        public bool Verified { get; init; }
    }
}
