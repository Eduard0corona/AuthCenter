using AuthCenter.Domain.Entities;

namespace AuthCenter.Application.Interfaces;

public interface IRefreshTokenService
{
    Task<RefreshToken> CreateAsync(Guid userId, string applicationCode, string tokenHash, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default);
    Task<RefreshToken?> FindByIdAsync(Guid tokenId, CancellationToken ct = default);
    Task<IReadOnlyList<RefreshToken>> GetActiveSessionsAsync(Guid userId, CancellationToken ct = default);
    Task RevokeAsync(RefreshToken token, string? replacedByHash, CancellationToken ct = default);
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);
    Task RevokeAllForUserAsync(Guid userId, string applicationCode, CancellationToken ct = default);
}
