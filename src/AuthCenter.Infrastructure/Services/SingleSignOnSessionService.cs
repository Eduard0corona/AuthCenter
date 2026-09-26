using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public sealed class SingleSignOnSessionService : ISingleSignOnSessionService
{
    private readonly AuthCenterDbContext _db;
    private readonly BackchannelLogoutQueue _backchannel;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;

    public SingleSignOnSessionService(AuthCenterDbContext db, BackchannelLogoutQueue backchannel, IAuditService audit, IDateTimeProvider clock)
    {
        _db = db;
        _backchannel = backchannel;
        _audit = audit;
        _clock = clock;
    }

    public async Task<OperationResult> EndSessionAsync(Guid userId, Guid sessionId, string reason, CancellationToken ct = default)
    {
        var session = await _db.RefreshTokens.FirstOrDefaultAsync(
            token => token.Id == sessionId && token.UserId == userId && token.OAuthClientId == null, ct);
        if (session is null)
            return OperationResult.Failure("SESSION_NOT_FOUND", "Session not found.");
        if (session.RevokedAt is not null)
            return OperationResult.Failure("SESSION_ALREADY_REVOKED", "Session is already revoked.");

        var now = _clock.UtcNow;
        session.RevokedAt = now;

        // The application grants the session authorized end with it: their refresh tokens would
        // otherwise keep the user signed in to those applications after signing out here.
        var grants = await _db.RefreshTokens
            .Where(token => token.UserId == userId && token.SessionId == sessionId && token.OAuthClientId != null && token.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var grant in grants)
            grant.RevokedAt = now;

        var notified = await _backchannel.EnqueueAsync(userId, [sessionId], null, ct);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return OperationResult.Failure("SESSION_ALREADY_REVOKED", "Session is already revoked.");
        }

        await _audit.LogAsync(
            "SSO_SESSION_ENDED",
            userId: userId,
            entityName: "Session",
            entityId: sessionId.ToString(),
            metadata: new { reason, revokedGrants = grants.Count, notifiedClients = notified },
            ct: ct);
        return OperationResult.Success();
    }
}
