using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthCenter.IntegrationTests;

public sealed class AdminFrontendHostingTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public AdminFrontendHostingTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AdminV2_Redirects_To_Trailing_Slash()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync("/admin-v2");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin-v2/", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/admin-v2/")]
    [InlineData("/admin-v2/users")]
    public async Task AdminV2_Serves_The_Spa_Without_Caching_Html(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("<div id=\"root\"></div>", body, StringComparison.Ordinal);
        Assert.Contains("/admin-v2/assets/", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminV2_Hashed_Assets_Are_Immutable()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/admin-v2/");
        var assetStart = html.IndexOf("/admin-v2/assets/", StringComparison.Ordinal);
        Assert.True(assetStart >= 0, "The SPA document must reference a versioned asset.");

        var assetEnd = html.IndexOf('"', assetStart);
        Assert.True(assetEnd > assetStart, "The SPA asset URL must be quoted.");
        var assetPath = html[assetStart..assetEnd];

        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
        using var response = await client.GetAsync(assetPath);

        response.EnsureSuccessStatusCode();
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
    }
}
