using AuthCenter.Api.Authorization;
using AuthCenter.Api.Extensions;
using AuthCenter.Api.Middleware;
using AuthCenter.Api.Services;
using AuthCenter.Application.Extensions;
using AuthCenter.Application.Interfaces;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Extensions;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Persistence.Seed;
using AuthCenter.Infrastructure.Security;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, config) =>
        config.ReadFrom.Configuration(ctx.Configuration)
              .ReadFrom.Services(services)
              .Enrich.FromLogContext());

    // Infrastructure (DbContext, Identity, services)
    builder.Services.AddInfrastructure(builder.Configuration);

    // Application (validators)
    builder.Services.AddApplication();
    builder.Services.AddPlatformObservability(builder.Configuration, builder.Environment);
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.MimeTypes = ["text/css", "text/javascript", "application/javascript"];
    });

    // CurrentUserService
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

    // JWT Authentication. The key ring is validated here so that a bad key fails startup rather
    // than the first request that needs to sign or verify a token.
    builder.Services.AddOptions<JwtSettings>().ValidateOnStart();
    builder.Services.AddOptions<MfaSettings>().ValidateOnStart();
    builder.Services.AddOptions<PasskeySettings>().ValidateOnStart();
    builder.Services.AddOptions<AdaptiveAuthenticationSettings>().ValidateOnStart();
    builder.Services.AddOptions<SamlSettings>().ValidateOnStart();

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = AuthenticationSchemes.Smart;
        options.DefaultAuthenticateScheme = AuthenticationSchemes.Smart;
        options.DefaultChallengeScheme = AuthenticationSchemes.Smart;
    })
    .AddPolicyScheme(AuthenticationSchemes.Smart, AuthenticationSchemes.Smart, options =>
    {
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? JwtBearerDefaults.AuthenticationScheme
                : AuthenticationSchemes.UiCookie;
    })
    .AddCookie(AuthenticationSchemes.UiCookie)
    .AddJwtBearer()
    .AddJwtBearer(AuthenticationSchemes.OAuthBearer);

    builder.Services.AddOptions<CookieAuthenticationOptions>(AuthenticationSchemes.UiCookie)
        .Configure(options =>
        {
            options.Cookie.Name = "__Host-AuthCenter.Ui";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
            options.Events.OnValidatePrincipal = async context =>
            {
                var subject = context.Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                    ?? context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                var session = context.Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sid)?.Value
                    ?? context.Principal?.FindFirst("sid")?.Value;
                if (!Guid.TryParse(subject, out var userId) || !Guid.TryParse(session, out var sessionId))
                {
                    context.RejectPrincipal();
                    return;
                }
                var db = context.HttpContext.RequestServices.GetRequiredService<AuthCenterDbContext>();
                var active = await db.RefreshTokens.AsNoTracking().AnyAsync(item =>
                    item.Id == sessionId && item.UserId == userId && item.RevokedAt == null &&
                    item.ExpiresAt > DateTime.UtcNow && item.User.IsActive,
                    context.HttpContext.RequestAborted);
                if (!active) context.RejectPrincipal();
            };
        });

    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<IOptions<JwtSettings>, RsaSigningKeyRing>((options, jwtOptions, keyRing) =>
        {
            var settings = jwtOptions.Value;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = settings.Issuer,
                ValidAudience = settings.Audience,
                IssuerSigningKeys = keyRing.ValidationKeys,
                ClockSkew = TimeSpan.Zero,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var subject = context.Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                        ?? context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    var session = context.Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sid)?.Value
                        ?? context.Principal?.FindFirst("sid")?.Value;

                    if (!Guid.TryParse(subject, out var userId) || !Guid.TryParse(session, out var sessionId))
                    {
                        context.Fail("The access token is not bound to an active session.");
                        return;
                    }

                    var db = context.HttpContext.RequestServices.GetRequiredService<AuthCenterDbContext>();
                    var now = DateTime.UtcNow;
                    var active = await db.RefreshTokens
                        .AsNoTracking()
                        .AnyAsync(token => token.Id == sessionId
                            && token.UserId == userId
                            && token.RevokedAt == null
                            && token.ExpiresAt > now
                            && token.User.IsActive,
                            context.HttpContext.RequestAborted);

                    if (!active)
                        context.Fail("The session has been revoked or is inactive.");
                }
            };
        });

    // OAuth access tokens are audience-scoped to the client that requested them, so the audience
    // cannot be pinned to a single value. They are still bound to this authorization server by
    // issuer and signature, and endpoints using this scheme additionally require the client_id
    // claim that only the token endpoint emits.
    builder.Services.AddOptions<JwtBearerOptions>(AuthenticationSchemes.OAuthBearer)
        .Configure<IOptions<JwtSettings>, RsaSigningKeyRing>((options, jwtOptions, keyRing) =>
        {
            var settings = jwtOptions.Value;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = false,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = settings.Issuer,
                IssuerSigningKeys = keyRing.ValidationKeys,
                ClockSkew = TimeSpan.Zero,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var clientId = context.Principal?.FindFirst("client_id")?.Value;
                    var audienceMatches = context.Principal?
                        .FindAll(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Aud)
                        .Any(claim => string.Equals(claim.Value, clientId, StringComparison.Ordinal)) == true;

                    if (string.IsNullOrWhiteSpace(clientId) || !audienceMatches)
                        context.Fail("The OAuth token audience does not match its client_id.");

                    return Task.CompletedTask;
                }
            };
        });

    // Authorization — dynamic permission policies
    builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
    builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
    builder.Services.AddAuthorization();

    // Rate Limiting
    var distributedRateLimiting = builder.Configuration.GetValue<bool>("RateLimiting:DistributedEnabled")
        && !builder.Environment.IsDevelopment()
        && !builder.Environment.IsEnvironment("Testing");
    if (!distributedRateLimiting && !builder.Environment.IsEnvironment("Testing"))
        builder.Services.AddAuthRateLimiting();
    if (distributedRateLimiting)
        builder.Services.AddScoped<DistributedRateLimitStore>();

    // Controllers
    builder.Services.AddControllers();

    // CORS
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("Default", policy =>
        {
            if (allowedOrigins.Length > 0)
            {
                policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            }
            else if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
            {
                // Wildcard is only intentional in local dev and automated test runs.
                policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
            }
            else
            {
                // Non-dev/test with no origins should have been caught by ValidateStartupConfiguration.
                // Fall back to localhost-only so a misconfigured staging is not wide open.
                policy.WithOrigins("http://localhost", "https://localhost").AllowAnyHeader().AllowAnyMethod();
            }
        });
    });

    // Swagger / OpenAPI
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "AuthCenter API", Version = "v1" });
        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Description = "JWT Authorization header. Enter: Bearer {token}",
            Name = "Authorization",
            In = ParameterLocation.Header,
            Type = SecuritySchemeType.ApiKey,
            Scheme = "Bearer"
        });
        c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecuritySchemeReference("Bearer", document),
                []
            }
        });
    });

    // Every rate limit partitions on the client address and every audit entry records it, so
    // behind a reverse proxy the forwarded headers have to be honoured or both end up seeing the
    // balancer instead of the caller — collapsing all callers into a single rate limit partition.
    var forwardedHeadersEnabled = builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled");
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        if (!forwardedHeadersEnabled)
        {
            options.ForwardedHeaders = ForwardedHeaders.None;
            return;
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = builder.Configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1;

        var configuredProxies = builder.Configuration
            .GetSection("ForwardedHeaders:KnownProxies")
            .Get<string[]>() ?? [];

        options.KnownProxies.Clear();
        foreach (var configuredProxy in configuredProxies)
        {
            if (!System.Net.IPAddress.TryParse(configuredProxy, out var proxyAddress))
                throw new InvalidOperationException($"ForwardedHeaders:KnownProxies contains invalid address '{configuredProxy}'.");

            options.KnownProxies.Add(proxyAddress);
        }
    });

    // Health Checks. Liveness carries no dependencies so a database blip does not get the
    // container restarted; readiness is the one that reports whether SQL Server is reachable.
    var connStr = builder.Configuration.GetConnectionString("DefaultConnection")!;
    builder.Services.AddHealthChecks()
        .AddSqlServer(connStr, name: "sql-server", tags: ["ready", "db", "sql"]);

    var app = builder.Build();
    var publishedAdminFrontendRoot = Path.Combine(app.Environment.WebRootPath, "admin-v2");
    var developmentAdminFrontendRoot = Path.GetFullPath(Path.Combine(
        app.Environment.ContentRootPath,
        "..",
        "AuthCenter.Admin",
        "dist"));
    var adminFrontendRoot = Directory.Exists(publishedAdminFrontendRoot)
        ? publishedAdminFrontendRoot
        : (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) &&
          Directory.Exists(developmentAdminFrontendRoot)
            ? developmentAdminFrontendRoot
            : null;

    // Validated after the host is built so that every configuration source is in play, including
    // ones contributed by the host itself.
    var jwtSettings = app.Configuration.GetSection("Jwt").Get<JwtSettings>()
        ?? throw new InvalidOperationException("Jwt configuration is required.");

    ValidateStartupConfiguration(app.Environment, app.Configuration, jwtSettings);

    // Database bootstrap. Defaults to on in Development; any other environment must opt in
    // explicitly through Database:MigrateOnStartup / Database:SeedOnStartup so that a deployed
    // instance can be initialized once without auto-migrating on every restart.
    var migrateOnStartup = app.Configuration.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment());
    var seedOnStartup = app.Configuration.GetValue("Database:SeedOnStartup", app.Environment.IsDevelopment());

    if (migrateOnStartup || seedOnStartup)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var lockAcquired = false;
        if (db.Database.IsRelational())
        {
            await db.Database.OpenConnectionAsync();
            await using var acquireCommand = db.Database.GetDbConnection().CreateCommand();
            acquireCommand.CommandText = "DECLARE @result int; EXEC @result = sp_getapplock @Resource = 'AuthCenter.DatabaseBootstrap', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 60000; SELECT @result;";
            var result = Convert.ToInt32(await acquireCommand.ExecuteScalarAsync());
            if (result < 0)
                throw new InvalidOperationException($"Could not acquire the database bootstrap lock (sp_getapplock result {result}).");
            lockAcquired = true;
        }

        try
        {
            if (migrateOnStartup)
            {
                logger.LogInformation("Applying database migrations on startup");
                await db.Database.MigrateAsync();
            }

            if (seedOnStartup)
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
                var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                logger.LogInformation("Seeding baseline application data on startup");
                await AuthCenterSeeder.SeedAsync(db, userManager, roleManager, config, logger);
            }
        }
        finally
        {
            if (lockAcquired)
            {
                await using var releaseCommand = db.Database.GetDbConnection().CreateCommand();
                releaseCommand.CommandText = "EXEC sp_releaseapplock @Resource = 'AuthCenter.DatabaseBootstrap', @LockOwner = 'Session';";
                await releaseCommand.ExecuteNonQueryAsync();
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    // Must run before anything that reads the client address or the scheme: rate limiting,
    // request logging and HTTPS redirection all depend on it.
    if (forwardedHeadersEnabled)
        app.UseForwardedHeaders();

    app.UseMiddleware<PlatformTelemetryMiddleware>();
    app.UseMiddleware<ExceptionHandlingMiddleware>();

    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        ctx.Response.Headers.Append("X-Frame-Options", "DENY");
        ctx.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
        ctx.Response.Headers.Append("Permissions-Policy", "camera=(), geolocation=(), microphone=(), payment=(), usb=()");
        if (ctx.Request.Path.StartsWithSegments("/login") ||
            ctx.Request.Path.Equals("/login.html") ||
            ctx.Request.Path.StartsWithSegments("/portal") ||
            ctx.Request.Path.Equals("/portal.html") ||
            ctx.Request.Path.StartsWithSegments("/admin") ||
            ctx.Request.Path.Equals("/admin.html") ||
            ctx.Request.Path.StartsWithSegments("/ui"))
        {
            ctx.Response.Headers.Append("Content-Security-Policy", "default-src 'self'; connect-src 'self'; img-src 'self' data: https:; script-src 'self'; style-src 'self'; font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'");
        }
        if (ctx.Request.Path.StartsWithSegments("/api/auth") ||
            ctx.Request.Path.StartsWithSegments("/oauth/token"))
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.Pragma = "no-cache";
        }
        await next();
    });

    app.UseSerilogRequestLogging();

    app.UseRouting();
    if (distributedRateLimiting)
        app.UseMiddleware<DistributedRateLimitMiddleware>();
    else if (!app.Environment.IsEnvironment("Testing"))
        app.UseRateLimiter();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "AuthCenter API v1"));
    }

    app.UseHttpsRedirection();
    app.UseResponseCompression();
    app.Use(async (context, next) =>
    {
        if (string.Equals(context.Request.Path.Value, "/admin-v2", StringComparison.Ordinal))
        {
            context.Response.Redirect("/admin-v2/");
            return;
        }

        if (context.Request.Path.StartsWithSegments("/admin-v2") &&
            !context.Request.Path.StartsWithSegments("/admin-v2/assets"))
        {
            context.Response.Headers.CacheControl = "no-store";
        }

        await next();
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = context =>
        {
            var requestPath = context.Context.Request.Path;
            context.Context.Response.Headers.CacheControl = context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                ? "no-store"
                : requestPath.StartsWithSegments("/admin-v2/assets")
                    ? "public,max-age=31536000,immutable"
                    : "public,max-age=86400";
        }
    });
    if (string.Equals(adminFrontendRoot, developmentAdminFrontendRoot, StringComparison.OrdinalIgnoreCase))
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(developmentAdminFrontendRoot),
            RequestPath = "/admin-v2",
            OnPrepareResponse = context =>
            {
                context.Context.Response.Headers.CacheControl = context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    ? "no-store"
                    : context.Context.Request.Path.StartsWithSegments("/admin-v2/assets")
                        ? "public,max-age=31536000,immutable"
                        : "public,max-age=86400";
            }
        });
    }
    app.UseCors("Default");
    app.UseAuthentication();
    app.UseMiddleware<UiCsrfMiddleware>();
    app.UseAuthorization();
    app.UseMiddleware<AdministrativeMutationAuditMiddleware>();
    app.MapControllers();
    app.MapGet("/", () => Results.Redirect("/login"));
    app.MapGet("/login", () => Results.File(
        Path.Combine(app.Environment.WebRootPath, "login.html"),
        "text/html; charset=utf-8"));
    app.MapGet("/portal", () => Results.File(
        Path.Combine(app.Environment.WebRootPath, "portal.html"),
        "text/html; charset=utf-8"));
    app.MapGet("/admin", () => Results.File(
        Path.Combine(app.Environment.WebRootPath, "admin.html"),
        "text/html; charset=utf-8"));
    app.MapFallback("/admin-v2/{*path:nonfile}", () =>
    {
        IResult result = adminFrontendRoot is not null
            ? Results.File(Path.Combine(adminFrontendRoot, "index.html"), "text/html; charset=utf-8")
            : Results.NotFound();
        return result;
    });
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    var readinessHost = app.Configuration["HealthChecks:ReadinessHost"];
    if (!string.IsNullOrWhiteSpace(readinessHost))
    {
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") })
            .RequireHost(readinessHost);
    }

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program
{
    private static void ValidateStartupConfiguration(IHostEnvironment environment, IConfiguration configuration, JwtSettings jwtSettings)
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            return;

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")))
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured outside Development.");

        if (!configuration.GetValue<bool>("RateLimiting:DistributedEnabled"))
            throw new InvalidOperationException("RateLimiting:DistributedEnabled must be true outside Development and Testing.");

        if (!Uri.TryCreate(jwtSettings.Issuer, UriKind.Absolute, out var issuerUri) || issuerUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Jwt:Issuer must be an absolute HTTPS URL outside Development.");

        var publicOrigin = configuration["Oidc:PublicOrigin"];
        if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var publicOriginUri) ||
            publicOriginUri.Scheme != Uri.UriSchemeHttps ||
            publicOrigin!.Contains("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Oidc:PublicOrigin must be a configured HTTPS URL outside Development.");
        }

        if (string.IsNullOrWhiteSpace(jwtSettings.SigningKey) ||
            jwtSettings.SigningKey.Length < 64 ||
            jwtSettings.SigningKey.StartsWith("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Jwt:SigningKey must be at least 64 characters and not use a placeholder value outside Development.");
        }

        var googleClientId = configuration["Authentication:Google:ClientId"];
        if (!string.IsNullOrWhiteSpace(googleClientId) &&
            googleClientId.StartsWith("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Authentication:Google:ClientId must not use the placeholder value outside Development.");
        }

        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        if (allowedOrigins.Length == 0)
            throw new InvalidOperationException("Cors:AllowedOrigins must contain at least one origin outside Development.");

        var allowedHosts = configuration["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts == "*")
            throw new InvalidOperationException("AllowedHosts must list the public hostnames outside Development.");

        if (configuration.GetValue<bool>("ForwardedHeaders:Enabled"))
        {
            var knownProxies = configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
            if (knownProxies.Length == 0)
                throw new InvalidOperationException("At least one ForwardedHeaders:KnownProxies address is required when forwarded headers are enabled.");
        }

        var actionLinkBaseUrl = configuration["ActionLinks:DefaultBaseUrl"];
        if (!Uri.TryCreate(actionLinkBaseUrl, UriKind.Absolute, out var actionLinkUri) ||
            actionLinkUri.Scheme != Uri.UriSchemeHttps ||
            actionLinkBaseUrl!.Contains("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ActionLinks:DefaultBaseUrl must be a configured HTTPS URL outside Development.");
        }

        var dataProtectionApplicationName = configuration["DataProtection:ApplicationName"];
        var dataProtectionCertificate = configuration["DataProtection:KeyEncryptionCertificateBase64"];
        if (string.IsNullOrWhiteSpace(dataProtectionApplicationName))
            throw new InvalidOperationException("DataProtection:ApplicationName is required outside Development.");
        if (string.IsNullOrWhiteSpace(dataProtectionCertificate) ||
            dataProtectionCertificate.StartsWith("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DataProtection keys must be protected with a configured PKCS#12 certificate outside Development.");
        }

        // An empty key silently disables MFA instead of failing, so it is rejected here rather
        // than only checking for the placeholder.
        var mfaKey = configuration["Mfa:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(mfaKey) ||
            mfaKey.Length < 32 ||
            mfaKey.StartsWith("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Mfa:EncryptionKey must be at least 32 characters and not use a placeholder value outside Development.");
        }

        var azureMonitorConnection = configuration["AzureMonitor:ConnectionString"];
        if (string.IsNullOrWhiteSpace(azureMonitorConnection) ||
            azureMonitorConnection.Contains("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase) ||
            !azureMonitorConnection.Contains("InstrumentationKey=", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("AzureMonitor:ConnectionString must be supplied by Key Vault outside Development/Testing.");
        }
    }
}
