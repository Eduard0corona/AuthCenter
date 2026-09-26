using System.Text.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

/// <summary>
/// Queues OpenID Connect back-channel logout notifications in the outbox, one per client that
/// received tokens through an ending session. The logout token itself is minted each time a
/// delivery is attempted, so a retry never sends an expired token.
/// </summary>
public sealed class BackchannelLogoutQueue
{
    internal const string MessageType = "backchannel-logout.v1";
    internal const string ProtectionPurpose = "AuthCenter.Outbox.BackchannelLogout.v1";
    internal const string HttpClientName = "BackchannelLogout";
    internal const int MaximumAttempts = 10;

    private readonly AuthCenterDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IDateTimeProvider _clock;

    public BackchannelLogoutQueue(AuthCenterDbContext db, IDataProtectionProvider protection, IDateTimeProvider clock)
    {
        _db = db;
        _protector = protection.CreateProtector(ProtectionPurpose);
        _clock = clock;
    }

    /// <summary>
    /// Adds, without saving, a notification for every active client with a back-channel logout URI
    /// that received tokens through one of the sessions, optionally only the clients of one application.
    /// </summary>
    public async Task<int> EnqueueAsync(Guid userId, IReadOnlyCollection<Guid> sessionIds, Guid? applicationSystemId, CancellationToken ct)
    {
        if (sessionIds.Count == 0)
            return 0;

        var targets = await _db.SingleSignOnSessionClients.AsNoTracking()
            .Where(item => item.UserId == userId &&
                sessionIds.Contains(item.SessionId) &&
                item.OAuthClient.IsActive &&
                item.OAuthClient.BackchannelLogoutUri != null &&
                (applicationSystemId == null || item.OAuthClient.ApplicationSystemId == applicationSystemId))
            .Select(item => new { item.SessionId, item.OAuthClient.ClientId })
            .ToListAsync(ct);

        var now = _clock.UtcNow;
        foreach (var target in targets)
        {
            _db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = MessageType,
                ProtectedPayload = _protector.Protect(JsonSerializer.Serialize(new Payload(target.ClientId, userId, target.SessionId))),
                CreatedAt = now,
                NextAttemptAt = now
            });
        }

        return targets.Count;
    }

    internal Payload Read(OutboxMessage message) =>
        JsonSerializer.Deserialize<Payload>(_protector.Unprotect(message.ProtectedPayload))
        ?? throw new InvalidOperationException("Back-channel logout payload is empty.");

    internal sealed record Payload(string ClientId, Guid UserId, Guid SessionId);
}
