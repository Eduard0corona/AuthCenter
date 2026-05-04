using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses.Auth;

namespace AuthCenter.Application.Interfaces;

public interface IAccountManagementService
{
    Task<OperationResult> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<SessionDto>> GetActiveSessionsAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult> RevokeSessionAsync(Guid userId, Guid tokenId, CancellationToken ct = default);
    Task<OperationResult> RevokeAllSessionsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<ExternalProviderDto>> GetExternalProvidersAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult> UnlinkExternalProviderAsync(Guid userId, Guid providerId, CancellationToken ct = default);
    Task<OperationResult> RequestEmailChangeAsync(Guid userId, RequestEmailChangeRequest request, CancellationToken ct = default);
    Task<OperationResult> ConfirmEmailChangeAsync(ConfirmEmailChangeRequest request, CancellationToken ct = default);
    Task<OperationResult> DeleteAccountAsync(Guid userId, DeleteAccountRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<TrustedDeviceDto>> GetTrustedDevicesAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult> RevokeTrustedDeviceAsync(Guid userId, Guid deviceId, CancellationToken ct = default);
    Task<OperationResult> RevokeAllTrustedDevicesAsync(Guid userId, CancellationToken ct = default);
}
