using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Contracts.Responses.OAuth;

namespace AuthCenter.Application.Interfaces;

public interface IOAuthAuthorizationService
{
    Task<OperationResult<string>> InitiateAuthorizationAsync(AuthorizeRequest request, CancellationToken ct = default);
    Task<OperationResult<string>> CompleteAuthorizationAsync(CompleteAuthorizationRequest request, Guid userId, CancellationToken ct = default);
    Task<OperationResult<OAuthTokenResponse>> ExchangeCodeAsync(OAuthTokenRequest request, CancellationToken ct = default);
    Task<OperationResult<OAuthTokenResponse>> ClientCredentialsAsync(OAuthTokenRequest request, CancellationToken ct = default);
    Task<OperationResult<OAuthTokenResponse>> RefreshOAuthTokenAsync(OAuthTokenRequest request, CancellationToken ct = default);
    Task<OperationResult<OAuthUserInfoResponse>> GetUserInfoAsync(Guid userId, IList<string> scopes, CancellationToken ct = default);
}
