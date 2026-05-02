using AuthCenter.Application.Interfaces;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public class RefreshTokenService : IRefreshTokenService
{
    private readonly AuthCenterDbContext _db;
    private readonly IDateTimeProvider _dateTimeProvider;

    public RefreshTokenService(AuthCenterDbContext db, IDateTimeProvider dateTimeProvider)
    {
        _db = db;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<RefreshToken> CreateAsync(Guid userId, string tokenHash, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = tokenHash,
            CreatedAt = _dateTimeProvider.UtcNow,
            ExpiresAt = _dateTimeProvider.UtcNow.AddDays(30),
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

    public async Task RevokeAsync(RefreshToken token, string? replacedByHash, CancellationToken ct = default)
    {
        token.RevokedAt = _dateTimeProvider.UtcNow;
        token.ReplacedByTokenHash = replacedByHash;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var tokens = await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null)
            .ToListAsync(ct);

        var now = _dateTimeProvider.UtcNow;
        foreach (var token in tokens)
            token.RevokedAt = now;

        await _db.SaveChangesAsync(ct);
    }
}
