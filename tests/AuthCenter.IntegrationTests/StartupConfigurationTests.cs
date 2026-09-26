using System.Net;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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

    [Fact]
    public void MissingAzureMonitorConnection_FailsStartupOutsideDevelopment()
    {
        using var factory = CreateProductionFactory(new Dictionary<string, string?> { ["AzureMonitor:ConnectionString"] = string.Empty });
        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("AzureMonitor:ConnectionString", exception.ToString());
    }

    [Fact]
    public void IssuerDifferentFromPublicOrigin_FailsStartupOutsideDevelopment()
    {
        // Relying-party libraries that enforce OpenID Connect Discovery reject a discovery document
        // whose issuer differs from the URL it is served from.
        using var factory = CreateProductionFactory(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "https://tokens.example.com"
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Oidc:PublicOrigin", exception.ToString());
    }

    [Fact]
    public void DevelopmentEmailPickupDirectory_FailsStartupOutsideDevelopment()
    {
        // Writing messages (with sign-in codes and links) to disk is only for development and tests.
        using var factory = CreateProductionFactory(new Dictionary<string, string?>
        {
            ["Email:DevelopmentPickupDirectory"] = Path.GetTempPath()
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Email:DevelopmentPickupDirectory", exception.ToString());
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
            ["Jwt:Issuer"] = "https://authcenter.example.com",
            ["Jwt:Audience"] = "authcenter-clients",
            ["Cors:AllowedOrigins:0"] = "https://app.example.com",
            ["AllowedHosts"] = "authcenter.example.com",
            ["ActionLinks:DefaultBaseUrl"] = "https://app.example.com",
            ["Oidc:PublicOrigin"] = "https://authcenter.example.com",
            ["DataProtection:ApplicationName"] = "AuthCenter.Tests",
            ["DataProtection:KeyEncryptionCertificateBase64"] = CreateCertificateBase64(),
            ["Mfa:EncryptionKey"] = "startup-test-mfa-encryption-key-32chars",
            ["Passkeys:RelyingPartyId"] = "authcenter.example.com",
            ["Passkeys:AllowedOrigins:0"] = "https://authcenter.example.com",
            ["AdaptiveAuth:SignalHashKey"] = "startup-test-adaptive-signal-key-32chars",
            ["Saml:EntityId"] = "https://authcenter.example.com/saml",
            ["Saml:AssertionConsumerServiceUrl"] = "https://authcenter.example.com/api/federation/saml/acs",
            ["Saml:SigningCertificateBase64"] = CreateCertificateBase64(),
            ["AzureMonitor:ConnectionString"] = "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://monitor.example.com/",
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

    private static string CreateCertificateBase64()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=AuthCenter Tests",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx));
    }
}
