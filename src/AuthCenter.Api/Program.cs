using System.Text;
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
    var jwtSettings = builder.Configuration.GetSection("Jwt").Get<JwtSettings>()
        ?? throw new InvalidOperationException("Jwt configuration is required.");

    ValidateStartupConfiguration(builder.Environment, builder.Configuration, jwtSettings);
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey)),
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
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

    // Migrate DB in Development
    if (app.Environment.IsDevelopment())
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        await db.Database.MigrateAsync();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        await AuthCenterSeeder.SeedAsync(db, userManager, roleManager, config, logger);
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

        var mfaKey = configuration["Mfa:EncryptionKey"];
        if (!string.IsNullOrWhiteSpace(mfaKey) &&
            mfaKey.StartsWith("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Mfa:EncryptionKey must not use a placeholder value outside Development.");
        }

        var rsaKey = jwtSettings.RsaPrivateKeyPem;
        if (!string.IsNullOrWhiteSpace(rsaKey) &&
            rsaKey.StartsWith("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Jwt:RsaPrivateKeyPem must not use a placeholder value outside Development.");
        }
    }
}
