using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.OAuth;

namespace AuthCenter.Application.Interfaces;

public interface IOAuthClientService
{
    Task<OperationResult<OAuthClientCreatedResponse>> CreateAsync(CreateOAuthClientRequest request, CancellationToken ct = default);
    Task<PagedResult<OAuthClientResponse>> GetAllAsync(PaginationQuery pagination, CancellationToken ct = default);
    Task<OAuthClientResponse?> GetByClientIdAsync(string clientId, CancellationToken ct = default);
    Task<OperationResult<OAuthClientResponse>> UpdateAsync(string clientId, UpdateOAuthClientRequest request, CancellationToken ct = default);
    Task<OperationResult> DeactivateAsync(string clientId, CancellationToken ct = default);
    Task<OperationResult<RotateClientSecretResponse>> RotateSecretAsync(string clientId, CancellationToken ct = default);
}
