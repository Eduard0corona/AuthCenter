using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Lifecycle;

namespace AuthCenter.Application.Interfaces;

public interface IProvisioningTokenService
{
    Task<PagedResult<ProvisioningTokenMetadataDto>> GetAsync(ProvisioningTokenQuery query, CancellationToken ct = default);
    Task<ProvisioningTokenMetadataDto?> GetByIdAsync(Guid tokenId, CancellationToken ct = default);
    Task<OperationResult<ProvisioningTokenResponse>> CreateAsync(CreateProvisioningTokenRequest request, CancellationToken ct = default);
    Task<OperationResult<ProvisioningTokenResponse>> RotateAsync(Guid tokenId, DateTime expiresAt, CancellationToken ct = default);
    Task<OperationResult> RevokeAsync(Guid tokenId, CancellationToken ct = default);
    Task<ProvisioningTokenCheck> AuthenticateAsync(string? rawToken, string requiredScope, CancellationToken ct = default);
    Task RecordRequestAsync(ScimRequestRecord request, CancellationToken ct = default);
    Task<ScimDiagnosticsDto?> GetDiagnosticsAsync(Guid tokenId, CancellationToken ct = default);
    Task<PagedResult<ScimRequestLogDto>?> GetRequestsAsync(Guid tokenId, ScimRequestLogQuery query, CancellationToken ct = default);
}
