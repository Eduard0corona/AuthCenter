using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public sealed class SsoAccessGate : ISsoAccessGate
{
    private readonly AuthCenterDbContext _db;
    private readonly IUserAccessService _userAccess;
    private readonly IAccessPolicyService _accessPolicies;
    private readonly IAuthenticationRiskService _authenticationRisk;
    private readonly IDateTimeProvider _clock;

    public SsoAccessGate(
        AuthCenterDbContext db,
        IUserAccessService userAccess,
        IAccessPolicyService accessPolicies,
        IAuthenticationRiskService authenticationRisk,
        IDateTimeProvider clock)
    {
        _db = db;
        _userAccess = userAccess;
        _accessPolicies = accessPolicies;
        _authenticationRisk = authenticationRisk;
        _clock = clock;
    }

    public async Task<SsoSessionInfo?> ResolveSessionAsync(Guid? userId, Guid? sessionId, CancellationToken ct = default)
    {
        if (userId is not { } user || sessionId is not { } session)
            return null;
        var now = _clock.UtcNow;
        // The single sign-on session is the browser's refresh token row that belongs to no client.
        var row = await _db.RefreshTokens.AsNoTracking()
            .Where(token => token.Id == session && token.UserId == user && token.OAuthClientId == null &&
                token.RevokedAt == null && token.ExpiresAt > now && token.User.IsActive)
            .Select(token => new { token.AuthenticatedAt, token.CreatedAt, token.AuthenticationMethods, token.AssuranceLevel })
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return null;
        var methods = AuthenticationContext.ParseMethods(row.AuthenticationMethods);
        return new SsoSessionInfo(
            user,
            session,
            row.AuthenticatedAt ?? row.CreatedAt,
            methods.Count > 0 ? methods : [DomainConstants.AuthenticationMethods.Password],
            row.AssuranceLevel is { } level && Enum.IsDefined(typeof(AuthenticationAssuranceLevel), level)
                ? (AuthenticationAssuranceLevel)level
                : AuthenticationAssuranceLevel.Password);
    }

    public async Task<SsoAccessDecision> EvaluateAsync(
        SsoSessionInfo session,
        Guid applicationSystemId,
        AuthenticationAssuranceLevel? requestedAssurance,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        if (!await _userAccess.HasActiveAccessAsync(session.UserId, applicationSystemId, ct))
            return new SsoAccessDecision(SsoAccessOutcome.Denied, session.Assurance, null, "NoApplicationAccess");

        var signals = await _authenticationRisk.AssessAndRecordAsync(session.UserId, ipAddress, userAgent, ct: ct);
        var policy = await _accessPolicies.EvaluateAsync(new AccessPolicyEvaluationContext(
            session.UserId,
            applicationSystemId,
            ipAddress,
            _clock.UtcNow,
            signals.RiskLevel,
            session.Assurance), ct);
        if (!policy.IsAllowed)
            return new SsoAccessDecision(SsoAccessOutcome.Denied, session.Assurance, policy, "AccessPolicy");

        var required = policy.RequiredAssuranceLevel;
        if (policy.RequireMfa && required < AuthenticationAssuranceLevel.Mfa)
            required = AuthenticationAssuranceLevel.Mfa;
        var applicationRequiresMfa = await _db.ApplicationRegistrationSettings.AsNoTracking()
            .AnyAsync(item => item.ApplicationSystemId == applicationSystemId && item.RequireMfa, ct);
        var userHasMfa = await _db.UserMfaCredentials.AsNoTracking()
            .AnyAsync(item => item.UserId == session.UserId && item.IsEnabled, ct);
        if ((applicationRequiresMfa || userHasMfa) && required < AuthenticationAssuranceLevel.Mfa)
            required = AuthenticationAssuranceLevel.Mfa;
        if (requestedAssurance is { } requested && requested > required)
            required = requested;

        return session.Assurance >= required
            ? new SsoAccessDecision(SsoAccessOutcome.Allowed, required, policy, "Allowed")
            : new SsoAccessDecision(SsoAccessOutcome.StepUp, required, policy, "StepUpRequired");
    }
}
