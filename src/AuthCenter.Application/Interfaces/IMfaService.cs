using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses.Auth;

namespace AuthCenter.Application.Interfaces;

public interface IMfaService
{
    Task<OperationResult<MfaSetupResponse>> SetupTotpAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult<BackupCodesResponse>> EnableTotpAsync(Guid userId, EnableMfaRequest request, CancellationToken ct = default);
    Task<bool> VerifyTotpCodeAsync(Guid userId, string code, CancellationToken ct = default);
    Task<bool> UseBackupCodeAsync(Guid userId, string code, CancellationToken ct = default);
    Task<OperationResult> DisableMfaAsync(Guid userId, DisableMfaRequest request, CancellationToken ct = default);
    Task<OperationResult<BackupCodesResponse>> RegenerateBackupCodesAsync(Guid userId, string totpCode, CancellationToken ct = default);
    Task<MfaStatusDto> GetStatusAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult> AdminResetMfaAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult> SetupEmailOtpAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult> EnableEmailOtpAsync(Guid userId, EnableEmailMfaRequest request, CancellationToken ct = default);
    Task<bool> SendMfaEmailOtpAsync(Guid userId, string pendingTokenJti, CancellationToken ct = default);
}
