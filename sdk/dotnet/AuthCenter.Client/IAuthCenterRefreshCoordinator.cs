namespace AuthCenter.Client;

/// <summary>
/// Deduplicates refresh-token rotation. Multi-instance applications must replace the default
/// implementation with a distributed coordinator that also shares the successful result briefly.
/// </summary>
public interface IAuthCenterRefreshCoordinator
{
    Task<OAuthTokenSet> CoordinateAsync(
        string refreshToken,
        Func<CancellationToken, Task<OAuthTokenSet>> refreshOperation,
        CancellationToken cancellationToken = default);
}
