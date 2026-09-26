using AuthCenter.Application.Models;
using AuthCenter.Application.Interfaces;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services;

public class RefreshTokenService : IRefreshTokenService
{
    private readonly AuthCenterDbContext _db;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly JwtSettings _jwtSettings;

    public RefreshTokenService(
        AuthCenterDbContext db,
        IDateTimeProvider dateTimeProvider,
        IOptions<JwtSettings> jwtSettings)
    {
        _db = db;
        _dateTimeProvider = dateTimeProvider;
        _jwtSettings = jwtSettings.Value;
    }

    public async Task<RefreshToken> CreateAsync(Guid userId, string applicationCode, string tokenHash, string? ipAddress, string? userAgent, AuthenticationContext? authentication = null, CancellationToken ct = default)
    {
        var context = authentication ?? AuthenticationContext.Password;
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ApplicationCode = applicationCode,
            TokenHash = tokenHash,
            CreatedAt = _dateTimeProvider.UtcNow,
            ExpiresAt = _dateTimeProvider.UtcNow.AddDays(_jwtSettings.RefreshTokenDays),
            IpAddress = ipAddress,
            UserAgent = userAgent,
            AuthenticatedAt = _dateTimeProvider.UtcNow,
            AuthenticationMethods = context.MethodsValue,
            AssuranceLevel = (int)context.Assurance
        };
        _db.RefreshTokens.Add(token);
        await _db.SaveChangesAsync(ct);
        return token;
    }

    public async Task<Guid> ContinueBrowserSessionAsync(Guid? currentSessionId, Guid userId, Guid newSessionId, CancellationToken ct = default)
    {
        if (currentSessionId is not { } currentId || currentId == newSessionId)
            return newSessionId;

        var now = _dateTimeProvider.UtcNow;
        var sessions = await _db.RefreshTokens
            .Where(token => (token.Id == currentId || token.Id == newSessionId) && token.OAuthClientId == null)
            .ToListAsync(ct);
        var current = sessions.FirstOrDefault(token => token.Id == currentId && token.RevokedAt == null && token.ExpiresAt > now);
        var fresh = sessions.FirstOrDefault(token => token.Id == newSessionId && token.UserId == userId && token.RevokedAt == null);
        if (current is null || fresh is null)
            return newSessionId;

        if (current.UserId != userId)
        {
            // Another account signed in on this browser, so the previous account's session ends.
            current.RevokedAt = now;
            await _db.SaveChangesAsync(ct);
            return newSessionId;
        }

        // The same account re-authenticated (prompt=login, max_age, step-up): the session and its
        // sid stay, and the latest authentication defines auth_time, amr and acr from now on.
        current.AuthenticatedAt = fresh.AuthenticatedAt;
        current.AuthenticationMethods = fresh.AuthenticationMethods;
        current.AssuranceLevel = fresh.AssuranceLevel;
        current.ApplicationCode = fresh.ApplicationCode;
        current.IpAddress = fresh.IpAddress;
        current.UserAgent = fresh.UserAgent;
        if (fresh.ExpiresAt > current.ExpiresAt)
            current.ExpiresAt = fresh.ExpiresAt;
        fresh.RevokedAt = now;
        await _db.SaveChangesAsync(ct);
        return current.Id;
    }

    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default) =>
        _db.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, ct);

    public Task<RefreshToken?> FindByIdAsync(Guid tokenId, CancellationToken ct = default) =>
        _db.RefreshTokens.FirstOrDefaultAsync(rt => rt.Id == tokenId, ct);

    public async Task<IReadOnlyList<RefreshToken>> GetActiveSessionsAsync(Guid userId, CancellationToken ct = default) =>
        await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null && rt.ExpiresAt > _dateTimeProvider.UtcNow)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task RevokeAsync(RefreshToken token, string? replacedByHash, CancellationToken ct = default)
    {
        token.RevokedAt = _dateTimeProvider.UtcNow;
        token.ReplacedByTokenHash = replacedByHash;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> TryRotateAsync(
        RefreshToken token,
        Guid replacementTokenId,
        string replacementTokenHash,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        var now = _dateTimeProvider.UtcNow;
        token.RevokedAt = now;
        token.ReplacedByTokenHash = replacementTokenHash;

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = replacementTokenId,
            UserId = token.UserId,
            ApplicationCode = token.ApplicationCode,
            TokenHash = replacementTokenHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_jwtSettings.RefreshTokenDays),
            IpAddress = ipAddress,
            UserAgent = userAgent,
            // A rotation continues the same authentication; it does not re-authenticate the user.
            AuthenticatedAt = token.AuthenticatedAt ?? token.CreatedAt,
            AuthenticationMethods = token.AuthenticationMethods,
            AssuranceLevel = token.AssuranceLevel
        });

        try
        {
            // EF wraps the update and insert in one transaction. RevokedAt is a concurrency token,
            // so only the request that observes the active token can commit a replacement.
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            foreach (var entry in _db.ChangeTracker.Entries<RefreshToken>().ToList())
                entry.State = EntityState.Detached;
            return false;
        }
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var now = _dateTimeProvider.UtcNow;
        if (_db.Database.IsRelational())
        {
            await _db.RefreshTokens
                .Where(rt => rt.UserId == userId && rt.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(rt => rt.RevokedAt, now), ct);
            return;
        }

        var tokens = await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in tokens)
            token.RevokedAt = now;

        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllForUserAsync(Guid userId, string applicationCode, CancellationToken ct = default)
    {
        var now = _dateTimeProvider.UtcNow;
        if (_db.Database.IsRelational())
        {
            await _db.RefreshTokens
                .Where(rt => rt.UserId == userId && rt.ApplicationCode == applicationCode && rt.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(rt => rt.RevokedAt, now), ct);
            return;
        }

        var tokens = await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.ApplicationCode == applicationCode && rt.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in tokens)
            token.RevokedAt = now;

        await _db.SaveChangesAsync(ct);
    }
}
