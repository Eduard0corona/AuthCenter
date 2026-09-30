using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Enums;

namespace AuthCenter.Application.Interfaces;

public interface IAuthService
{
    Task<OperationResult<AuthResponse>> RegisterAsync(RegisterRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);

    /// <summary>
    /// Tells the owner of an existing account that someone tried to register with its address, and
    /// returns what a new registration in that application would have been told
    /// (<c>APPROVAL_REQUIRED</c> or <c>EMAIL_CONFIRMATION_REQUIRED</c>): the hosted sign-up answers
    /// the attempt the same way, so it reveals no account.
    /// </summary>
    Task<string> NotifyRegistrationAttemptAsync(string email, string applicationCode, string? ipAddress, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> GoogleLoginAsync(GoogleLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> MicrosoftLoginAsync(MicrosoftLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> GitHubLoginAsync(GitHubLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> AppleLoginAsync(AppleLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> VerifyMfaAsync(VerifyMfaRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);

    /// <summary>
    /// Starts a step-up for a user who already holds a session: fails with <c>MFA_REQUIRED</c> and a
    /// pending token for <see cref="VerifyMfaAsync"/>, or with the reason the level cannot be reached
    /// (<c>MFA_SETUP_REQUIRED</c>, <c>PASSKEY_REQUIRED</c>, <c>PASSKEY_ENROLLMENT_REQUIRED</c>).
    /// </summary>
    Task<OperationResult<AuthResponse>> BeginStepUpAsync(Guid userId, string applicationCode, AuthenticationAssuranceLevel requiredAssurance, string primaryMethod, string? ipAddress, string? userAgent, CancellationToken ct = default);

    /// <summary>
    /// Finishes a sign-in whose identity an upstream provider already proved: the application's
    /// access and policy gate still runs, and a second factor is asked for unless the upstream
    /// authentication (a trusted upstream MFA) already reaches the required assurance.
    /// </summary>
    Task<OperationResult<AuthResponse>> CompleteFederatedSignInAsync(Guid userId, string applicationCode, AuthenticationContext authentication, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult> SendMfaEmailOtpAsync(SendMfaEmailOtpRequest request, CancellationToken ct = default);

    /// <summary>
    /// Sign-in enrollment: when the application requires MFA (<c>MFA_SETUP_REQUIRED</c>) or a
    /// passkey (<c>PASSKEY_ENROLLMENT_REQUIRED</c>) and the user has none, the failure message is a
    /// single-use enrollment token. These methods set up the authenticator app with it and finish
    /// the sign-in once its first code is verified.
    /// </summary>
    Task<OperationResult<MfaSetupResponse>> BeginTotpEnrollmentAsync(string enrollmentToken, CancellationToken ct = default);
    Task<OperationResult<MfaEnrollmentResult>> CompleteTotpEnrollmentAsync(string enrollmentToken, string totpCode, string? ipAddress, string? userAgent, CancellationToken ct = default);

    /// <summary>The user a passkey enrollment token belongs to, or <c>null</c> when it is invalid.</summary>
    Task<Guid?> GetPasskeyEnrollmentUserAsync(string enrollmentToken, CancellationToken ct = default);
    Task CompletePasskeyEnrollmentAsync(string enrollmentToken, string? ipAddress, string? userAgent, CancellationToken ct = default);
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
