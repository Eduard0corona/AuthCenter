using System.Text.Json;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuthCenter.Infrastructure.Services;

public sealed class OutboxDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<OutboxDispatcherService> _logger;

    public OutboxDispatcherService(
        IServiceScopeFactory scopeFactory,
        IDataProtectionProvider protectionProvider,
        ILogger<OutboxDispatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = protectionProvider.CreateProtector("AuthCenter.Outbox.Email.v1");
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Outbox dispatch cycle failed");
            }
        }
    }

    internal async Task DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        if (!db.Database.IsRelational())
            return;
        var sender = scope.ServiceProvider.GetRequiredService<SmtpEmailService>();
        var now = DateTime.UtcNow;
        var candidateIds = await db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.ProcessedAt == null && message.NextAttemptAt <= now && (message.LockedUntil == null || message.LockedUntil < now))
            .OrderBy(message => message.CreatedAt)
            .Select(message => message.Id)
            .Take(20)
            .ToListAsync(ct);

        foreach (var id in candidateIds)
        {
            var lockUntil = now.AddMinutes(2);
            var claimed = await db.OutboxMessages
                .Where(message => message.Id == id && message.ProcessedAt == null && (message.LockedUntil == null || message.LockedUntil < now))
                .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.LockedUntil, lockUntil), ct);
            if (claimed != 1)
                continue;

            var message = await db.OutboxMessages.SingleAsync(item => item.Id == id, ct);
            try
            {
                var payload = JsonSerializer.Deserialize<OutboxEmailService.EmailPayload>(_protector.Unprotect(message.ProtectedPayload))
                    ?? throw new InvalidOperationException("Outbox email payload is empty.");
                await SendAsync(sender, payload, ct);
                message.ProcessedAt = DateTime.UtcNow;
                message.LockedUntil = null;
                message.LastError = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.AttemptCount++;
                message.LockedUntil = null;
                message.NextAttemptAt = DateTime.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(message.AttemptCount, 5))));
                message.LastError = exception.Message.Length <= 2000 ? exception.Message : exception.Message[..2000];
                _logger.LogError(exception, "Failed to dispatch outbox message {OutboxMessageId}", message.Id);
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private static Task SendAsync(SmtpEmailService sender, OutboxEmailService.EmailPayload payload, CancellationToken ct) =>
        payload.Kind switch
        {
            "password-reset" => sender.SendPasswordResetAsync(payload.ToEmail, payload.ToName, payload.Secret, payload.ActionUrl, ct),
            "email-confirmation" => sender.SendEmailConfirmationAsync(payload.ToEmail, payload.ToName, payload.Secret, payload.ActionUrl, ct),
            "invitation" => sender.SendInvitationAsync(payload.ToEmail, payload.ToName, payload.ApplicationName ?? string.Empty, payload.Secret, payload.ActionUrl, ct),
            "email-change" => sender.SendEmailChangeConfirmationAsync(payload.ToEmail, payload.ToName, payload.Secret, payload.ActionUrl, ct),
            "magic-link" => sender.SendMagicLinkAsync(payload.ToEmail, payload.ToName, payload.Secret, payload.ActionUrl, ct),
            "mfa-otp" => sender.SendMfaEmailOtpAsync(payload.ToEmail, payload.ToName, payload.Secret, ct),
            _ => throw new InvalidOperationException($"Unsupported outbox email kind '{payload.Kind}'.")
        };
}
