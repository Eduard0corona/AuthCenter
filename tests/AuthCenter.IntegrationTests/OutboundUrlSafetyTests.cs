using System.Net;
using AuthCenter.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>Event hooks only ever reach public addresses (SSRF protection).</summary>
public sealed class OutboundUrlSafetyTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public OutboundUrlSafetyTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("93.184.215.14", true)]
    [InlineData("2606:4700:10::6814:179a", true)]
    [InlineData("64:ff9b::808:808", true)]          // NAT64 form of 8.8.8.8
    [InlineData("10.0.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]          // cloud metadata
    [InlineData("172.20.1.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("203.0.113.5", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd12:3456::1", false)]             // unique local
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("64:ff9b::a00:1", false)]           // NAT64 form of 10.0.0.1
    [InlineData("2002:a00:1::1", false)]            // 6to4 form of 10.0.0.1
    [InlineData("2001:db8::1", false)]
    public void Addresses_AreClassifiedAsPublicOrNot(string address, bool expected) =>
        Assert.Equal(expected, OutboundUrlSafety.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task TheHookClient_RefusesToConnectToAnInternalAddress()
    {
        using var client = _factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("EventHooks");

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://127.0.0.1:9/hook"));

        Assert.Contains("does not resolve only to public addresses", failure.ToString(), StringComparison.Ordinal);
    }
}
