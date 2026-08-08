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

    public async Task<RefreshToken> CreateAsync(Guid userId, string applicationCode, string tokenHash, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ApplicationCode = applicationCode,
            TokenHash = tokenHash,
            CreatedAt = _dateTimeProvider.UtcNow,
            ExpiresAt = _dateTimeProvider.UtcNow.AddDays(_jwtSettings.RefreshTokenDays),
            IpAddress = ipAddress,
            UserAgent = userAgent
        };
        _db.RefreshTokens.Add(token);
        await _db.SaveChangesAsync(ct);
        return token;
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
            UserAgent = userAgent
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
