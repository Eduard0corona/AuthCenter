using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AuthCenter.IntegrationTests;

public class StartupConfigurationTests
{
    [Fact]
    public void MissingRsaPrivateKey_FailsStartupExplicitly()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Jwt:RsaPrivateKeyPem"] = string.Empty
                    }));
            });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Jwt:RsaPrivateKeyPem", exception.ToString());
        Assert.Contains("valid RSA private key", exception.ToString());
    }
}
