using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses.Auth;

namespace AuthCenter.Application.Interfaces;

public interface IReauthenticationService
{
    Task<OperationResult<ReauthenticationProofResponse>> VerifyPasswordAsync(Guid userId, PasswordReauthenticationRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<PasskeyOptionsResponse>> GetPasskeyOptionsAsync(Guid userId, BeginPasskeyStepUpRequest request, CancellationToken ct = default);
    Task<OperationResult<ReauthenticationProofResponse>> VerifyPasskeyAsync(Guid userId, CompletePasskeyStepUpRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<bool> ConsumeProofAsync(Guid userId, string purpose, string? proofToken, CancellationToken ct = default);
}
