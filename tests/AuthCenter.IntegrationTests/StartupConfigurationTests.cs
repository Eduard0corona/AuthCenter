using System.Net;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public void EmptyMfaEncryptionKey_FailsStartupOutsideDevelopment()
    {
        // An empty key used to pass validation and silently disable MFA at runtime.
        using var factory = CreateProductionFactory(new Dictionary<string, string?>
        {
            ["Mfa:EncryptionKey"] = string.Empty
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Mfa:EncryptionKey", exception.ToString());
    }

    [Fact]
    public async Task ProductionHost_AcceptsRequestsForItsPublicHostname()
    {
        // Host filtering runs before everything else, so a hostname outside AllowedHosts is
        // rejected with 400 before any endpoint is reached.
        using var factory = CreateProductionFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Host = "authcenter.example.com";

        var response = await client.GetAsync("/.well-known/jwks.json");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateProductionFactory(
        IDictionary<string, string?>? overrides = null)
    {
        var databaseName = "AuthCenterStartup_" + Guid.NewGuid();

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=unused;Trusted_Connection=True;",
            ["Jwt:SigningKey"] = new string('k', 64),
            ["Jwt:RsaPrivateKeyPem"] = TestRsaKey.PrivateKeyPem,
            ["Cors:AllowedOrigins:0"] = "https://app.example.com",
            ["Mfa:EncryptionKey"] = "startup-test-mfa-encryption-key-32chars",
            ["Authentication:Google:ClientId"] = string.Empty,
            ["Database:MigrateOnStartup"] = "false",
            ["Database:SeedOnStartup"] = "false"
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
                settings[key] = value;
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings));

            builder.ConfigureServices(services =>
            {
                var dbContextDescriptors = services
                    .Where(d =>
                        d.ServiceType == typeof(AuthCenterDbContext) ||
                        d.ServiceType == typeof(DbContextOptions) ||
                        d.ServiceType == typeof(DbContextOptions<AuthCenterDbContext>) ||
                        d.ServiceType.FullName?.Contains("IDbContextOptionsConfiguration", StringComparison.Ordinal) == true)
                    .ToList();

                foreach (var descriptor in dbContextDescriptors)
                    services.Remove(descriptor);

                services.AddDbContext<AuthCenterDbContext>(options =>
                    options.UseInMemoryDatabase(databaseName));
            });
        });
    }
}
