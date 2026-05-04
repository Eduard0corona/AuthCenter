using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses.Auth;

namespace AuthCenter.Application.Interfaces;

public interface IAuthService
{
    Task<OperationResult<AuthResponse>> RegisterAsync(RegisterRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> GoogleLoginAsync(GoogleLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> MicrosoftLoginAsync(MicrosoftLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> GitHubLoginAsync(GitHubLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> AppleLoginAsync(AppleLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> VerifyMfaAsync(VerifyMfaRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult> SendMfaEmailOtpAsync(SendMfaEmailOtpRequest request, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> ForcedChangePasswordAsync(ForcedChangePasswordRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult> SendMagicLinkAsync(MagicLinkRequest request, string? ipAddress, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> VerifyMagicLinkAsync(VerifyMagicLinkRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> RefreshTokenAsync(string refreshToken, string? applicationCode, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult> LogoutAsync(Guid userId, string? refreshToken, CancellationToken ct = default);
    Task<OperationResult> RevokeTokenAsync(string refreshToken, Guid requestingUserId, CancellationToken ct = default);
    Task<OperationResult> ForgotPasswordAsync(ForgotPasswordRequest request, string? ipAddress, CancellationToken ct = default);
    Task<OperationResult> ResetPasswordAsync(ResetPasswordRequest request, string? ipAddress, CancellationToken ct = default);
    Task<OperationResult> ConfirmEmailAsync(ConfirmEmailRequest request, string? ipAddress, CancellationToken ct = default);
    Task<OperationResult> ResendEmailConfirmationAsync(ResendEmailConfirmationRequest request, string? ipAddress, CancellationToken ct = default);
}
