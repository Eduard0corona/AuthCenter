using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Contracts.Responses.OAuth;

namespace AuthCenter.Application.Interfaces;

public interface IOAuthAuthorizationService
{
    Task<OperationResult<AuthorizationEndpointResult>> InitiateAuthorizationAsync(AuthorizeRequest request, AuthorizationCaller caller, CancellationToken ct = default);
    Task<OperationResult<OAuthInteractionContextResponse>> GetInteractionContextAsync(string interactionId, string? browserBinding, CancellationToken ct = default);
    Task<OperationResult<OAuthInteractionResponse>> GetInteractionAsync(string interactionId, AuthorizationCaller caller, CancellationToken ct = default);
    Task<OperationResult<AuthorizationResponse>> CompleteAuthorizationAsync(CompleteAuthorizationRequest request, AuthorizationCaller caller, CancellationToken ct = default);
    Task<string> StorePendingResponseAsync(AuthorizationResponse response, string? browserBinding, CancellationToken ct = default);
    Task<AuthorizationResponse?> TakePendingResponseAsync(string responseId, string? browserBinding, CancellationToken ct = default);
    Task<OperationResult<OAuthTokenResponse>> ExchangeCodeAsync(OAuthTokenRequest request, CancellationToken ct = default);
    Task<OperationResult<OAuthTokenResponse>> ClientCredentialsAsync(OAuthTokenRequest request, CancellationToken ct = default);
    Task<OperationResult<OAuthTokenResponse>> RefreshOAuthTokenAsync(OAuthTokenRequest request, CancellationToken ct = default);
    Task<OperationResult> RevokeTokenAsync(OAuthRevocationRequest request, CancellationToken ct = default);
    Task<OperationResult<OAuthUserInfoResponse>> GetUserInfoAsync(Guid userId, string clientId, IList<string> scopes, CancellationToken ct = default);
    Task<IReadOnlyList<OAuthConsentGrantDto>> GetConsentGrantsAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult> RevokeConsentGrantAsync(Guid userId, Guid grantId, CancellationToken ct = default);
}
