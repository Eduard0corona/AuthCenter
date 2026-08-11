using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses.Auth;

namespace AuthCenter.Application.Interfaces;

public interface IPasskeyService
{
    Task<OperationResult<PasskeyOptionsResponse>> GetRegistrationOptionsAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult<PasskeyCredentialDto>> RegisterAsync(Guid userId, RegisterPasskeyRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<IReadOnlyList<PasskeyCredentialDto>> GetAllAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult> RenameAsync(Guid userId, string credentialId, RenamePasskeyRequest request, CancellationToken ct = default);
    Task<OperationResult> RemoveAsync(Guid userId, string credentialId, CancellationToken ct = default);
    Task<OperationResult<PasskeyOptionsResponse>> GetLoginOptionsAsync(BeginPasskeyLoginRequest request, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> LoginAsync(CompletePasskeyLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
}
