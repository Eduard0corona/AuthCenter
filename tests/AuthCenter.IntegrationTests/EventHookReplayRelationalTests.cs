using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Requests.Audit;
using AuthCenter.Contracts.Responses.Audit;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuthCenter.IntegrationTests;

public sealed class EventHookReplayRelationalTests
{
    [RelationalFact]
    public async Task DeliveryThatDeadLettersAgain_CanBeReplayedAgainWithTheSameLegacyKey()
    {
        var connectionString = SqlServerHardeningTests.BuildIsolatedConnectionString();
        var options = SqlServerHardeningTests.CreateOptions(connectionString);
        var deliveryId = Guid.NewGuid();
        var legacyKey = $"legacy:{deliveryId:N}";
        try
        {
            await using (var setup = new AuthCenterDbContext(options))
            {
                await setup.Database.MigrateAsync();
                var hook = new EventHook
                {
                    Id = Guid.NewGuid(),
                    Name = "Loopback hook",
                    // A loopback destination is never a public HTTPS endpoint, so every delivery fails
                    // deterministically without network access.
                    Url = "https://127.0.0.1/hook",
                    ProtectedSecret = "unused",
                    EventTypesJson = "[\"USER_CREATED\"]",
                    IsVerified = true,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                setup.EventHooks.Add(hook);
                setup.EventHookDeliveries.Add(new EventHookDelivery
                {
                    Id = deliveryId,
                    EventHookId = hook.Id,
                    EventId = Guid.NewGuid(),
                    EventType = "USER_CREATED",
                    PayloadJson = "{}",
                    AttemptCount = 9,
                    NextAttemptAt = DateTime.UtcNow.AddMinutes(-1),
                    // A previous replay was already accepted with the legacy key.
                    LastReplayIdempotencyKey = legacyKey
                });
                await setup.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddScoped(_ => new AuthCenterDbContext(options));
            services.AddHttpClient();
            await using var provider = services.BuildServiceProvider();
            var dispatcher = new EventHookDispatcherService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IHttpClientFactory>(),
                new EphemeralDataProtectionProvider(),
                NullLogger<EventHookDispatcherService>.Instance);

            await dispatcher.DispatchBatchAsync(CancellationToken.None);

            await using (var afterFailure = new AuthCenterDbContext(options))
            {
                var delivery = await afterFailure.EventHookDeliveries.AsNoTracking().SingleAsync(item => item.Id == deliveryId);
                Assert.NotNull(delivery.DeadLetteredAt);
                Assert.Null(delivery.LastReplayIdempotencyKey);
            }

            await using (var replayDb = new AuthCenterDbContext(options))
            {
                var hooks = new EventHookService(replayDb, new DateTimeProvider(), new NoOpAuditService(), new EphemeralDataProtectionProvider(), provider.GetRequiredService<IHttpClientFactory>());
                var replay = await hooks.ReplayDeadLetterAsync(deliveryId, legacyKey);
                Assert.True(replay.IsSuccess);
            }

            await using var verify = new AuthCenterDbContext(options);
            var requeued = await verify.EventHookDeliveries.AsNoTracking().SingleAsync(item => item.Id == deliveryId);
            Assert.Null(requeued.DeadLetteredAt);
            Assert.Equal(0, requeued.AttemptCount);
            Assert.Equal(legacyKey, requeued.LastReplayIdempotencyKey);
        }
        finally
        {
            await using var cleanup = new AuthCenterDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, Guid? userId = null, string? applicationCode = null, string? entityName = null, string? entityId = null, string? ipAddress = null, string? userAgent = null, object? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PagedResult<AuditLogDto>> GetAsync(AuditLogQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AuditLogExport> ExportAsync(AuditLogQuery query, int maxRows, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
