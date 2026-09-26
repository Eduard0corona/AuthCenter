using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Api.Controllers;

[ApiController, Route("api/admin-dashboard"), Authorize(Policy = DomainConstants.Permissions.AuditLogsRead)]
public sealed class AdminDashboardController(AuthCenterDbContext db, IDateTimeProvider clock, ISeparationOfDutiesService separationOfDuties) : ControllerBase
{
    private static readonly string[] FailedSignInActions =
        ["LOGIN_FAILED", "LOGIN_LOCKED_OUT", "MFA_VERIFY_FAILED", "PASSKEY_LOGIN_FAILED", "FEDERATION_LOGIN_FAILED"];

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var now = clock.UtcNow; var since = now.AddHours(-24); var soon = now.AddDays(30);
        var dto = new AdminDashboardDto
        {
            GeneratedAt = now,
            ActiveUsers = await db.Users.CountAsync(x => x.IsActive && x.DeletedAt == null, ct),
            InactiveUsers = await db.Users.IgnoreQueryFilters().CountAsync(x => (!x.IsActive || x.DeletedAt != null), ct),
            ActiveApplications = await db.ApplicationSystems.CountAsync(x => x.IsActive, ct),
            ActiveGroups = await db.DirectoryGroups.CountAsync(x => x.IsActive, ct),
            PendingAccessRequests = await db.AccessRequests.CountAsync(x => x.Status == AccessRequestStatus.Pending && (x.ExpiresAt == null || x.ExpiresAt > now) && x.User.IsActive && x.User.DeletedAt == null, ct),
            ActiveAccessReviews = await db.AccessReviewCampaigns.CountAsync(x => x.Status == AccessReviewStatus.Active, ct),
            OverdueAccessReviews = await db.AccessReviewCampaigns.CountAsync(x => x.Status == AccessReviewStatus.Active && x.DueAt < now, ct),
            PendingAccessReviewItems = await db.AccessReviewItems.CountAsync(x => x.Decision == AccessReviewDecision.Pending && x.Campaign.Status == AccessReviewStatus.Active, ct),
            SeparationOfDutiesViolations = await separationOfDuties.CountViolationsAsync(ct),
            ActiveFederationProviders = await db.FederationProviders.CountAsync(x => x.IsActive, ct),
            ExpiringProvisioningTokens = await db.ProvisioningTokens.CountAsync(x => x.RevokedAt == null && x.ExpiresAt > now && x.ExpiresAt <= soon, ct),
            UnverifiedEventHooks = await db.EventHooks.CountAsync(x => x.IsActive && !x.IsVerified, ct),
            DeadLetterDeliveries = await db.EventHookDeliveries.CountAsync(x => x.DeadLetteredAt != null, ct),
            FailedLoginsLast24Hours = await db.AuditLogs.CountAsync(x => x.CreatedAt >= since && FailedSignInActions.Contains(x.Action), ct),
            HighRiskObservationsLast24Hours = await db.AuthenticationObservations.CountAsync(x => x.ObservedAt >= since && (x.RiskLevel == AccessRiskLevel.High || x.RiskLevel == AccessRiskLevel.Critical), ct)
        };
        return Ok(ApiResponse<object>.Ok(dto));
    }
}
