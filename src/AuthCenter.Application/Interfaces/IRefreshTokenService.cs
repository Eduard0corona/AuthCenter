using AuthCenter.Application.Models;
using AuthCenter.Domain.Entities;

namespace AuthCenter.Application.Interfaces;

public interface IRefreshTokenService
{
    Task<RefreshToken> CreateAsync(Guid userId, string applicationCode, string tokenHash, string? ipAddress, string? userAgent, AuthenticationContext? authentication = null, CancellationToken ct = default);
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>
    /// Joins a new hosted-login sign-in to the single sign-on session the browser already holds.
    /// The same account re-authenticating keeps that session, and the sid clients know, with the
    /// new authentication time and methods; another account signing in ends the previous session.
    /// Returns the session the browser continues with.
    /// </summary>
    Task<Guid> ContinueBrowserSessionAsync(Guid? currentSessionId, Guid userId, Guid newSessionId, CancellationToken ct = default);
    Task<RefreshToken?> FindByIdAsync(Guid tokenId, CancellationToken ct = default);
    Task<IReadOnlyList<RefreshToken>> GetActiveSessionsAsync(Guid userId, CancellationToken ct = default);
    Task RevokeAsync(RefreshToken token, string? replacedByHash, CancellationToken ct = default);
    Task<bool> TryRotateAsync(
        RefreshToken token,
        Guid replacementTokenId,
        string replacementTokenHash,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default);
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);
    Task RevokeAllForUserAsync(Guid userId, string applicationCode, CancellationToken ct = default);
}
