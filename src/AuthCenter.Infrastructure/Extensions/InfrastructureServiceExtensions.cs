using AuthCenter.Application.Interfaces;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Security;
using AuthCenter.Infrastructure.Services;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Extensions;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddDbContext<AuthCenterDbContext>(options => ConfigureSqlServer(options, connectionString));
        services.AddDbContextFactory<AuthCenterDbContext>(
            options => ConfigureSqlServer(options, connectionString),
            ServiceLifetime.Scoped);

        services.AddMemoryCache();

        services.Configure<JwtSettings>(configuration.GetSection("Jwt"));
        services.AddSingleton<IValidateOptions<JwtSettings>, JwtSettingsValidator>();
        services.AddSingleton<RsaSigningKeyRing>();
        services.Configure<GoogleAuthSettings>(configuration.GetSection("Authentication:Google"));
        services.Configure<MicrosoftAuthSettings>(configuration.GetSection("Authentication:Microsoft"));
        services.Configure<GitHubAuthSettings>(configuration.GetSection("Authentication:GitHub"));
        services.Configure<AppleAuthSettings>(configuration.GetSection("Authentication:Apple"));
        services.Configure<EmailSettings>(configuration.GetSection("Email"));
        services.Configure<MfaSettings>(configuration.GetSection("Mfa"));

        services.AddHttpClient("GitHub", client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AuthCenter/1.0");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
        {
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = false;
            options.User.RequireUniqueEmail = true;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.MaxFailedAccessAttempts = 5;
        })
        .AddEntityFrameworkStores<AuthCenterDbContext>()
        .AddDefaultTokenProviders();

        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ITransientStateStore, TransientStateStore>();
        services.AddScoped<IGoogleAuthService, GoogleAuthService>();
        services.AddScoped<IMicrosoftAuthService, MicrosoftAuthService>();
        services.AddScoped<IGitHubAuthService, GitHubAuthService>();
        services.AddScoped<IAppleAuthService, AppleAuthService>();
        services.AddScoped<IApplicationService, ApplicationService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IUserAccessService, UserAccessService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IEmailService, SmtpEmailService>();
        services.AddScoped<IAccountManagementService, AccountManagementService>();
        services.AddScoped<IMfaService, TotpService>();
        services.AddScoped<IOAuthClientService, OAuthClientService>();
        services.AddScoped<IOAuthAuthorizationService, OAuthAuthorizationService>();

        return services;
    }

    private static void ConfigureSqlServer(DbContextOptionsBuilder options, string? connectionString)
    {
        options.UseSqlServer(connectionString, sql =>
        {
            sql.MigrationsAssembly(typeof(AuthCenterDbContext).Assembly.FullName);

            // Azure SQL drops connections routinely for throttling and failover. Without a retry
            // strategy those transient faults surface to callers as 500s.
            sql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null);

            sql.CommandTimeout(30);
        });
    }
}
