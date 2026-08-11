using Microsoft.AspNetCore.Http;

namespace AuthCenter.Client;

public interface IAuthCenterBffSessionManager
{
    Task<string?> GetAccessTokenAsync(HttpContext context, CancellationToken cancellationToken = default);
    Task<AuthCenterBffRefreshResult> RefreshAsync(HttpContext context, CancellationToken cancellationToken = default);
    Task RevokeAndSignOutAsync(HttpContext context, CancellationToken cancellationToken = default);
}

public sealed record AuthCenterBffRefreshResult(
    bool Succeeded,
    string? ErrorCode = null,
    string? AccessToken = null);
