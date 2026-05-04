using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuthCenter.IntegrationTests;

public class AuthCenterWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin.integration@example.com";
    public const string AdminPassword = "Admin12345";

    private readonly InMemoryDatabaseRoot _databaseRoot = new();
    private readonly string _databaseName = "AuthCenterTest_" + Guid.NewGuid();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed:AdminEmail"] = AdminEmail,
                ["Seed:AdminPassword"] = AdminPassword,
                ["Seed:AdminFullName"] = "Integration Admin",
                ["Mfa:EncryptionKey"] = "integration-test-mfa-encryption-key-32chars",
                ["Mfa:TotpIssuer"] = "AuthCenter",
                ["Mfa:MfaTokenExpirySeconds"] = "300",
                ["Jwt:RsaPrivateKeyPem"] = "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQCjdZQG+0HEosHE\nd5AbVNS7yPY3zAe+RSLK0UWXdvpJfZelPy401/WGRZNLdQ7MbkuV4SJqODhsr5QV\nlDyBsqCG16YWrZAjeroHQh7yXadVBFf+9LLWhK0ngrFJKEco6QMazBlZTNbBKJ3b\np5FerhhzySOjWe5yg1W6jcPEP5crVFk0IoZb9qaFhLxfvgMtPiVXrmEJ5IrzBKL7\nt+6gBnFimagIxHOw5WusEwnXIdC6mCGsha0JETrXMn9j4cxgl6OyzUtNlBo65kd0\nOIE+s35jOS61n/oit9f055Gk3mUmdAH8F6dyL/gHY/R+J1YLtwNqqiK5b49424H5\nXgN2UPS1AgMBAAECggEABzh3bIOG2TfTVWlurTZnG1B6R+a0ZNxK+CicaV9xGPP6\nUetjkCQGDYNfSVMHb1Jj9l/2lDidjXeIFBfzQEtyueImzROnrVmLhCyQj2ZBsQi7\nFmEa0U0VFQy55iBoXE9GutVPPVmelvPDXWOekU8hd2PoNhwP43EXxWDjx/SDKf/C\nxkNAlloCC1Uj7zUv6CB+Ti4I5d//h0nd1+kbS684tZKCBGFq0LehpZFeWE0uY3lA\nS+Pxp8jKWt5jCOi4SuB5wsqy9TVM84OV/chD1J11tEckv/NlfP6T8hNhuOiNGG5B\nVN+GzEMrEWXGxtiRDN3FxJj2uLf6eSkAsPck/tsb1QKBgQDEGaQXLUYW6nnMOjcO\napKI/7KW/B9WOiP6pWAljn/s5hKJhiCkSCu/9ffwLyk4vjAhf1S0SUEhNOpBeggr\nUE2TQQ9u7N5FfoXzi88hm3WaQGEv+82ptv9x/5krNTarCYAewOgwsMX3MbxeEPxC\n34tyaVQ4dO+lT47jrFWpKceJnwKBgQDVY4iHRRkMWQ8SEoEcFVDBsHhgvl7ydslj\npe7NgiMtfb8hcDVYjK0U7GZFolqC4dI5M0WG905zryCLyuwBIeg0w8MwTfDMcKJW\nyx6CT+qm4OPA5/fbe+WXJ65zf3WtHaENIobFkFbfn7okYyU/b9Vhky15o1JEycEu\nFpNG7uHJKwKBgDoOBUA/TQK3w/ssOORxtJQObwa3+WcDq2cm0oIL3994dYB1TvCT\n1S/tV8upiqCb3Y/tLFx7W2bDTZ64y/ZWvcCcgD8srNkSDgH+IAIpDfXunVchSs+1\nt4Y+T1A1Xsvf8igooBTaIKF//e4zRRtfJt9mQ2K7a78bAIhymqGMFuCtAoGBAMnl\nJY0O1Hc5YCfyOBQRGtpXlF28YOs5dqwAmGP6yDvHaOpDV0XTvAM69DzYMpj4/kU+\nfH8JSEHuJXX+ZbNgmGor+maep8FWpFJIVjJnWJXR28tt7rXMkNqmIcmHhBhqGzHp\nQ+hy+68TWjQJVnmh3xs1/GXFIBaTaWKo22nWpOwHAoGAdazybXeOKYKVPfAX9PGs\nefDNbgP1dxNRjfTU+KLfr8b7wfDrrD3iW3281i/xtN1y1JflxpFTV/x/XXRV5SIZ\nwAjtKnM/IyOKm436qtPGNj3VLCoDonR121UZzS7zkEg+v7lnGtvmV9eY0mm+e0zx\nkWlRGEa2pDduZ8Q1jLocy4s=\n-----END PRIVATE KEY-----"
            });
        });

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
                options.UseInMemoryDatabase(_databaseName, _databaseRoot));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("TestSeed");

        AuthCenterSeeder
            .SeedAsync(db, userManager, roleManager, configuration, logger)
            .GetAwaiter()
            .GetResult();

        return host;
    }
}
