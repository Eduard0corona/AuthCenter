using System.Security.Cryptography;
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
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
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

    // CurrentUserService
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

    // JWT Authentication
    builder.Services.AddOptions<JwtSettings>()
        .Validate(
            settings => IsValidRsaPrivateKey(settings.RsaPrivateKeyPem),
            "Jwt:RsaPrivateKeyPem must be configured with a valid RSA private key of at least 2048 bits in PEM format and must not use a placeholder value.")
        .ValidateOnStart();

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer()
    .AddJwtBearer(AuthenticationSchemes.OAuthBearer);

    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<IOptions<JwtSettings>>((options, jwtOptions) =>
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
                IssuerSigningKey = CreateRsaValidationKey(settings),
                ClockSkew = TimeSpan.Zero,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
            };
        });

    // OAuth access tokens are audience-scoped to the client that requested them, so the audience
    // cannot be pinned to a single value. They are still bound to this authorization server by
    // issuer and signature, and endpoints using this scheme additionally require the client_id
    // claim that only the token endpoint emits.
    builder.Services.AddOptions<JwtBearerOptions>(AuthenticationSchemes.OAuthBearer)
        .Configure<IOptions<JwtSettings>>((options, jwtOptions) =>
        {
            var settings = jwtOptions.Value;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = false,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = settings.Issuer,
                IssuerSigningKey = CreateRsaValidationKey(settings),
                ClockSkew = TimeSpan.Zero,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
            };
        });

    // Authorization — dynamic permission policies
    builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
    builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
    builder.Services.AddAuthorization();

    // Rate Limiting
    if (!builder.Environment.IsEnvironment("Testing"))
        builder.Services.AddAuthRateLimiting();

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
                policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
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
        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                },
                []
            }
        });
    });

    // Health Checks
    var connStr = builder.Configuration.GetConnectionString("DefaultConnection")!;
    builder.Services.AddHealthChecks()
        .AddSqlServer(connStr, name: "sql-server", tags: ["db", "sql"]);

    var app = builder.Build();

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

    app.UseMiddleware<ExceptionHandlingMiddleware>();

    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        ctx.Response.Headers.Append("X-Frame-Options", "DENY");
        ctx.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
        await next();
    });

    app.UseSerilogRequestLogging();

    if (!app.Environment.IsEnvironment("Testing"))
        app.UseRateLimiter();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "AuthCenter API v1"));
    }

    app.UseHttpsRedirection();
    app.UseCors("Default");
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapHealthChecks("/health");

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
    private static bool IsValidRsaPrivateKey(string rsaPrivateKeyPem)
    {
        if (string.IsNullOrWhiteSpace(rsaPrivateKeyPem) ||
            rsaPrivateKeyPem.StartsWith("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(rsaPrivateKeyPem);
            _ = rsa.ExportParameters(true);
            return rsa.KeySize >= 2048;
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    private static RsaSecurityKey CreateRsaValidationKey(JwtSettings jwtSettings)
    {
        if (string.IsNullOrWhiteSpace(jwtSettings.RsaPrivateKeyPem) ||
            jwtSettings.RsaPrivateKeyPem.StartsWith("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Jwt:RsaPrivateKeyPem must be configured with a valid RSA private key in PEM format and must not use a placeholder value.");
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(jwtSettings.RsaPrivateKeyPem);

            if (rsa.KeySize < 2048)
                throw new CryptographicException("The RSA key must be at least 2048 bits.");

            _ = rsa.ExportParameters(true);
            return new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = JwtSettings.RsaKeyId };
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new InvalidOperationException(
                "Jwt:RsaPrivateKeyPem must contain a valid RSA private key of at least 2048 bits in PEM format.",
                exception);
        }
    }

    private static void ValidateStartupConfiguration(IHostEnvironment environment, IConfiguration configuration, JwtSettings jwtSettings)
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            return;

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")))
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured outside Development.");

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

        // An empty key silently disables MFA instead of failing, so it is rejected here rather
        // than only checking for the placeholder.
        var mfaKey = configuration["Mfa:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(mfaKey) ||
            mfaKey.Length < 32 ||
            mfaKey.StartsWith("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Mfa:EncryptionKey must be at least 32 characters and not use a placeholder value outside Development.");
        }
    }
}
