using AuthCenter.Contracts.Responses.Auth;

namespace AuthCenter.Application.Models;

/// <summary>A sign-in completed by enrolling the authenticator app, with its one-time backup codes.</summary>
public sealed record MfaEnrollmentResult(AuthResponse Session, IReadOnlyList<string> BackupCodes);
