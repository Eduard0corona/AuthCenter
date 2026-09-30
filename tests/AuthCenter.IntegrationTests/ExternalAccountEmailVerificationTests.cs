using System.Net;
using System.Net.Http.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// An account created from a social provider carries email_verified only when the provider vouches
/// for the address. A Microsoft work account's email is whatever its tenant's administrators set.
/// </summary>
public class ExternalAccountEmailVerificationTests
{
    private const string ApplicationCode = "SOCIALOPEN";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MicrosoftLogin_CreatesTheAccount_VerifiedOnlyWhenMicrosoftVouchesForTheEmail(bool emailVerified)
    {
        var email = $"microsoft-{Guid.NewGuid():N}@example.com";
        using var factory = new MicrosoftStubWebApplicationFactory(new ExternalTokenPayload
        {
            Subject = $"tenant:{Guid.NewGuid():N}",
            Email = email,
            Name = "Tenant User",
            EmailVerified = emailVerified
        });
        using var client = factory.CreateClient();
        await CreateOpenApplicationAsync(factory);

        var response = await client.PostAsJsonAsync("/api/auth/microsoft", new MicrosoftLoginRequest
        {
            IdToken = "stubbed-id-token",
            ApplicationCode = ApplicationCode
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.Equal(emailVerified, user.EmailConfirmed);
    }

    private static async Task CreateOpenApplicationAsync(AuthCenterWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var now = DateTime.UtcNow;
        db.ApplicationSystems.Add(new ApplicationSystem
        {
            Id = Guid.NewGuid(),
            Code = ApplicationCode,
            Name = ApplicationCode,
            IsActive = true,
            CreatedAt = now,
            RegistrationSettings = new ApplicationRegistrationSettings
            {
                Id = Guid.NewGuid(),
                RegistrationMode = ApplicationRegistrationMode.Open,
                AllowMicrosoftLogin = true,
                AllowPasswordLogin = true,
                CreatedAt = now
            }
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Replaces the Microsoft token validation, which needs Microsoft's signing keys.</summary>
    private sealed class MicrosoftStubWebApplicationFactory(ExternalTokenPayload payload) : AuthCenterWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<IMicrosoftAuthService>(new StubMicrosoftAuthService(payload)));
        }
    }

    private sealed class StubMicrosoftAuthService(ExternalTokenPayload payload) : IMicrosoftAuthService
    {
        public Task<ExternalTokenPayload?> ValidateIdTokenAsync(string idToken, CancellationToken ct = default)
            => Task.FromResult<ExternalTokenPayload?>(payload);
    }
}
