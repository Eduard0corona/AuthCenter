using System.Text.Json;
using System.Text.RegularExpressions;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Services;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// On SQL Server, a queued email goes out through the outbox dispatcher in the name of the
/// application it was queued for, with the lifetime of its link and a plain-text version; the
/// system application the migrations create names no product.
/// </summary>
public sealed class TransactionalEmailRelationalTests
{
    [RelationalFact]
    public async Task QueuedEmail_IsSentByTheDispatcher_InTheApplicationsName()
    {
        var options = SqlServerHardeningTests.CreateOptions(SqlServerHardeningTests.BuildIsolatedConnectionString());
        var protection = new EphemeralDataProtectionProvider();
        var pickup = Directory.CreateTempSubdirectory("authcenter-mail-").FullName;
        try
        {
            await using (var setup = new AuthCenterDbContext(options))
            {
                await setup.Database.MigrateAsync();
                setup.ApplicationSystems.Add(new ApplicationSystem
                {
                    Id = Guid.NewGuid(),
                    Code = "SHIP",
                    Name = "Envíos (interno)",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    BrandingSettings = new ApplicationBrandingSettings
                    {
                        Id = Guid.NewGuid(), DisplayName = "Paquetenvia", SupportUrl = "https://ayuda.paquetenvia.example/", CreatedAt = DateTime.UtcNow
                    }
                });
                await setup.SaveChangesAsync();
                var emails = new OutboxEmailService(setup, protection);
                await emails.SendMagicLinkAsync("ana@example.com", "Ana Muñoz", "token", "https://login.example/magic-link?application=SHIP");
                await emails.SendPasswordResetAsync("ana@example.com", "Ana Muñoz", "token", "https://login.example/reset-password?application=AUTHCENTER");
            }

            var services = new ServiceCollection();
            services.AddScoped(_ => new AuthCenterDbContext(options));
            services.AddLogging();
            services.AddHttpClient();
            services.AddSingleton(Options.Create(new EmailSettings { DevelopmentPickupDirectory = pickup }));
            services.AddSingleton(Options.Create(new JwtSettings { MagicLinkTokenMinutes = 12 }));
            services.AddSingleton(Options.Create(new DataProtectionTokenProviderOptions()));
            services.AddScoped<SmtpEmailService>();
            await using var provider = services.BuildServiceProvider();
            var dispatcher = new OutboxDispatcherService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                protection,
                provider.GetRequiredService<IHttpClientFactory>(),
                NullLogger<OutboxDispatcherService>.Instance);

            await dispatcher.DispatchBatchAsync(CancellationToken.None);

            var mails = await Task.WhenAll(Directory.GetFiles(pickup, "*.json").Select(async file => JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement));
            Assert.Equal(2, mails.Length);
            var mail = Assert.Single(mails, item => item.GetProperty("purpose").GetString() == "Magic link");
            Assert.Equal("ana@example.com", mail.GetProperty("to").GetString());
            Assert.Equal("Tu enlace para entrar a Paquetenvia", mail.GetProperty("subject").GetString());
            Assert.StartsWith("<!doctype html>", mail.GetProperty("html").GetString());
            Assert.Contains("href=\"https://login.example/magic-link?application=SHIP&amp;token=token&amp;email=ana%40example.com\"", mail.GetProperty("html").GetString());
            Assert.Contains("El enlace vence en 12 minutos y sólo funciona una vez.", mail.GetProperty("text").GetString());
            Assert.Contains("Paquetenvia · Ayuda: https://ayuda.paquetenvia.example/", mail.GetProperty("text").GetString());
            // The system application, with the branding row the migrations gave it, names no product.
            var reset = Assert.Single(mails, item => item.GetProperty("purpose").GetString() == "Password reset");
            Assert.Equal("Restablece tu contraseña", reset.GetProperty("subject").GetString());
            foreach (var part in new[] { "subject", "html", "text" })
                Assert.DoesNotContain("authcenter", Regex.Replace(reset.GetProperty(part).GetString()!, @"https?://[^\s""<]+", string.Empty), StringComparison.OrdinalIgnoreCase);
            await using var check = new AuthCenterDbContext(options);
            Assert.All(await check.OutboxMessages.Where(item => item.Type == "email.v1").ToListAsync(), message =>
            {
                Assert.NotNull(message.ProcessedAt);
                Assert.Null(message.LastError);
            });
        }
        finally
        {
            Directory.Delete(pickup, recursive: true);
            await using var cleanup = new AuthCenterDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
