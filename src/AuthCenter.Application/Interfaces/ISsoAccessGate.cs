using AuthCenter.Application.Models;
using AuthCenter.Domain.Enums;

namespace AuthCenter.Application.Interfaces;

/// <summary>
/// The single decision of whether a browser's single sign-on session may be used for an application
/// now, shared by every protocol AuthCenter signs users in with (OpenID Connect, SAML).
/// </summary>
public interface ISsoAccessGate
{
    /// <summary>The live single sign-on session the browser presents, if any.</summary>
    Task<SsoSessionInfo?> ResolveSessionAsync(Guid? userId, Guid? sessionId, CancellationToken ct = default);

    /// <summary>
    /// The user's access to the application, its published access policy (with this browser's address
    /// and risk), the application's and the user's MFA, and any assurance the caller requested.
    /// </summary>
    Task<SsoAccessDecision> EvaluateAsync(
        SsoSessionInfo session,
        Guid applicationSystemId,
        AuthenticationAssuranceLevel? requestedAssurance,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default);
}

public sealed record SsoSessionInfo(Guid UserId, Guid SessionId, DateTime AuthenticatedAt, IReadOnlyList<string> Methods, AuthenticationAssuranceLevel Assurance);

public enum SsoAccessOutcome { Allowed, StepUp, Denied }

/// <summary>A session below the required assurance can be stepped up; a denial cannot.</summary>
public sealed record SsoAccessDecision(SsoAccessOutcome Outcome, AuthenticationAssuranceLevel RequiredAssurance, AccessPolicyDecision? Policy, string Reason);
