using AuthCenter.Application.Models;

namespace AuthCenter.Application.Interfaces;

public interface IGitHubAuthService
{
    Task<ExternalTokenPayload?> GetUserFromAccessTokenAsync(string accessToken, CancellationToken ct = default);
}
