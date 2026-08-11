using System.Security.Cryptography;
using System.Text;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuthCenter.Infrastructure.Services;

public sealed class EventHookDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes; private readonly IHttpClientFactory _clients; private readonly IDataProtector _protector; private readonly ILogger<EventHookDispatcherService> _logger;
    public EventHookDispatcherService(IServiceScopeFactory scopes, IHttpClientFactory clients, IDataProtectionProvider protection, ILogger<EventHookDispatcherService> logger) { _scopes = scopes; _clients = clients; _protector = protection.CreateProtector("AuthCenter.EventHookSecrets.v1"); _logger = logger; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5)); while (await timer.WaitForNextTickAsync(stoppingToken)) try { await DispatchBatchAsync(stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; } catch (Exception ex) { _logger.LogError(ex, "Event hook dispatch cycle failed"); } }
    internal async Task DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>(); if (!db.Database.IsRelational()) return; var now = DateTime.UtcNow;
        var ids = await db.EventHookDeliveries.AsNoTracking().Where(x => x.DeliveredAt == null && x.DeadLetteredAt == null && x.NextAttemptAt <= now && (x.LockedUntil == null || x.LockedUntil < now)).OrderBy(x => x.NextAttemptAt).Select(x => x.Id).Take(20).ToListAsync(ct);
        foreach (var id in ids)
        {
            var claimed = await db.EventHookDeliveries.Where(x => x.Id == id && x.DeliveredAt == null && x.DeadLetteredAt == null && (x.LockedUntil == null || x.LockedUntil < now)).ExecuteUpdateAsync(s => s.SetProperty(x => x.LockedUntil, now.AddMinutes(2)), ct); if (claimed != 1) continue;
            var delivery = await db.EventHookDeliveries.Include(x => x.EventHook).SingleAsync(x => x.Id == id, ct);
            try
            {
                if (!delivery.EventHook.IsActive || !delivery.EventHook.IsVerified) throw new InvalidOperationException("Event hook is inactive or unverified.");
                if (!await OutboundUrlSafety.IsPublicHttpsAsync(delivery.EventHook.Url, ct)) throw new InvalidOperationException("Event hook URL is not a public HTTPS destination.");
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture); var secret = _protector.Unprotect(delivery.EventHook.ProtectedSecret); var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{delivery.PayloadJson}"))).ToLowerInvariant();
                using var request = new HttpRequestMessage(HttpMethod.Post, delivery.EventHook.Url) { Content = new StringContent(delivery.PayloadJson, Encoding.UTF8, "application/json") }; request.Headers.Add("X-AuthCenter-Event-Id", delivery.EventId.ToString()); request.Headers.Add("X-AuthCenter-Idempotency-Key", delivery.EventId.ToString()); request.Headers.Add("X-AuthCenter-Timestamp", timestamp); request.Headers.Add("X-AuthCenter-Signature", $"v1={signature}");
                using var response = await _clients.CreateClient("EventHooks").SendAsync(request, ct); if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Event hook returned HTTP {(int)response.StatusCode}."); delivery.DeliveredAt = DateTime.UtcNow; delivery.LockedUntil = null; delivery.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                delivery.AttemptCount++; delivery.LockedUntil = null; delivery.LastError = ex.Message.Length <= 2000 ? ex.Message : ex.Message[..2000]; if (delivery.AttemptCount >= 10) delivery.DeadLetteredAt = DateTime.UtcNow; else delivery.NextAttemptAt = DateTime.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(delivery.AttemptCount, 5)))); _logger.LogWarning(ex, "Event hook delivery {DeliveryId} failed on attempt {Attempt}", id, delivery.AttemptCount);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
