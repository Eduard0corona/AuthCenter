using AuthCenter.Application.Interfaces;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Security;
using AuthCenter.Infrastructure.Services;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;

namespace AuthCenter.Infrastructure.Extensions;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddDbContextPool<AuthCenterDbContext>(options => ConfigureSqlServer(options, connectionString));
        services.AddPooledDbContextFactory<AuthCenterDbContext>(options => ConfigureSqlServer(options, connectionString));

        services.AddMemoryCache();

        var dataProtection = services.AddDataProtection()
            .SetApplicationName(configuration["DataProtection:ApplicationName"] ?? "AuthCenter")
            .PersistKeysToDbContext<AuthCenterDbContext>();

        var certificateBase64 = configuration["DataProtection:KeyEncryptionCertificateBase64"];
        if (!string.IsNullOrWhiteSpace(certificateBase64) &&
            !certificateBase64.StartsWith("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
        {
            var certificateBytes = Convert.FromBase64String(certificateBase64);
            var certificate = X509CertificateLoader.LoadPkcs12(
                certificateBytes,
                configuration["DataProtection:KeyEncryptionCertificatePassword"],
                X509KeyStorageFlags.EphemeralKeySet);
            dataProtection.ProtectKeysWithCertificate(certificate);
        }

        services.Configure<JwtSettings>(configuration.GetSection("Jwt"));
        services.AddSingleton<IValidateOptions<JwtSettings>, JwtSettingsValidator>();
        services.AddSingleton<RsaSigningKeyRing>();
        services.Configure<GoogleAuthSettings>(configuration.GetSection("Authentication:Google"));
        services.Configure<MicrosoftAuthSettings>(configuration.GetSection("Authentication:Microsoft"));
        services.Configure<GitHubAuthSettings>(configuration.GetSection("Authentication:GitHub"));
        services.Configure<AppleAuthSettings>(configuration.GetSection("Authentication:Apple"));
        services.Configure<EmailSettings>(configuration.GetSection("Email"));
        services.Configure<SingleSignOnSettings>(configuration.GetSection("Sso"));
        services.AddSingleton<IValidateOptions<SingleSignOnSettings>, SingleSignOnSettingsValidator>();
        services.Configure<MfaSettings>(configuration.GetSection("Mfa"));
        services.AddSingleton<IValidateOptions<MfaSettings>, MfaSettingsValidator>();
        services.Configure<PasskeySettings>(configuration.GetSection("Passkeys"));
        services.AddSingleton<IValidateOptions<PasskeySettings>, PasskeySettingsValidator>();
        services.Configure<AdaptiveAuthenticationSettings>(configuration.GetSection("AdaptiveAuth"));
        services.AddSingleton<IValidateOptions<AdaptiveAuthenticationSettings>, AdaptiveAuthenticationSettingsValidator>();
        services.Configure<SamlSettings>(configuration.GetSection("Saml"));
        services.Configure<OidcSettings>(configuration.GetSection("Oidc"));
        services.AddSingleton<IValidateOptions<SamlSettings>, SamlSettingsValidator>();
        services.Configure<ActionLinkSettings>(configuration.GetSection("ActionLinks"));
        services.Configure<RetentionSettings>(configuration.GetSection("Retention"));
        services.AddHostedService<RetentionCleanupService>();
        services.Configure<GovernanceSettings>(configuration.GetSection("Governance"));

        services.AddHttpClient("GitHub", client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AuthCenter/1.0");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        // Upstream metadata and token requests go to the exact configured URLs, never to a redirect.
        services.AddHttpClient(FederationMetadataCache.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<FederationMetadataCache>();
        // Hooks call administrator-configured URLs: never a redirect, and only public addresses at the
        // moment of connecting, so neither a redirect nor a DNS answer that changes reaches an internal host.
        services.AddHttpClient("EventHooks", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = OutboundUrlSafety.ConnectToPublicAddressAsync
            });
        // Logout tokens are posted to the exact registered URI; a redirect is a failed delivery.
        services.AddHttpClient(BackchannelLogoutQueue.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

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
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            options.Stores.MaxLengthForKeys = 450;
        })
        .AddEntityFrameworkStores<AuthCenterDbContext>()
        .AddDefaultTokenProviders();

        services.AddOptions<IdentityPasskeyOptions>()
            .Configure<IOptions<PasskeySettings>>((options, configured) =>
            {
                var settings = configured.Value;
                options.ServerDomain = string.IsNullOrWhiteSpace(settings.RelyingPartyId)
                    ? "localhost"
                    : settings.RelyingPartyId;
                options.AuthenticatorTimeout = TimeSpan.FromMinutes(settings.CeremonyMinutes);
                options.ChallengeSize = 64;
                options.UserVerificationRequirement = "required";
                options.ResidentKeyRequirement = "preferred";
                options.AttestationConveyancePreference = "none";
                if (settings.AllowedOrigins.Length > 0)
                {
                    var origins = settings.AllowedOrigins.ToHashSet(StringComparer.Ordinal);
                    options.ValidateOrigin = context => ValueTask.FromResult(origins.Contains(context.Origin));
                }
            });

        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<BackchannelLogoutQueue>();
        services.AddScoped<ISingleSignOnSessionService, SingleSignOnSessionService>();
        services.AddScoped<IEndSessionService, EndSessionService>();
        services.AddScoped<IApiResourceService, ApiResourceService>();
        services.AddScoped<IAuthenticationSessionIssuer, AuthenticationSessionIssuer>();
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
        services.AddScoped<SmtpEmailService>();
        services.AddScoped<IEmailService, OutboxEmailService>();
        services.AddHostedService<OutboxDispatcherService>();
        services.AddSingleton<IActionLinkService, ActionLinkService>();
        services.AddScoped<IAccountManagementService, AccountManagementService>();
        services.AddScoped<IMfaService, TotpService>();
        services.AddScoped<IOAuthClientService, OAuthClientService>();
        services.AddScoped<IOAuthAuthorizationService, OAuthAuthorizationService>();
        services.AddScoped<IExternalIdentityLinkService, ExternalIdentityLinkService>();
        services.AddScoped<IDirectoryGroupService, DirectoryGroupService>();
        services.AddScoped<IAccessPolicyService, AccessPolicyService>();
        services.AddScoped<IUserProfileService, UserProfileService>();
        services.AddScoped<DynamicGroupMembershipService>();
        services.AddScoped<ISsoAccessGate, SsoAccessGate>();
        services.AddSingleton<Services.Saml.SamlIdentityProviderKeys>();
        services.AddScoped<ISamlIdentityProviderService, Services.Saml.SamlIdentityProviderService>();
        services.AddScoped<ISamlServiceProviderService, Services.Saml.SamlServiceProviderService>();
        services.AddScoped<ISeparationOfDutiesChecker, Services.Governance.SeparationOfDutiesChecker>();
        services.AddScoped<ISeparationOfDutiesService, Services.Governance.SeparationOfDutiesService>();
        services.AddScoped<IAccessGovernanceService, Services.Governance.AccessGovernanceService>();
        services.AddScoped<IAccessReviewService, Services.Governance.AccessReviewService>();
        services.AddHostedService<Services.Governance.GovernanceMaintenanceService>();
        services.AddScoped<IPasskeyService, PasskeyService>();
        services.AddScoped<IReauthenticationService, ReauthenticationService>();
        services.AddScoped<IAuthenticationRiskService, AuthenticationRiskService>();
        services.AddScoped<IFederationService, FederationService>();
        services.AddScoped<IProvisioningTokenService, ProvisioningTokenService>();
        services.AddScoped<IScimService, ScimService>();
        services.AddScoped<ILifecycleAutomationService, LifecycleAutomationService>();
        services.AddScoped<IEventHookService, EventHookService>();
        services.AddHostedService<EventHookDispatcherService>();

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
