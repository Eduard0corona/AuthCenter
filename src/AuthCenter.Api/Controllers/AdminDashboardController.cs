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
public sealed class AdminDashboardController(AuthCenterDbContext db, IDateTimeProvider clock) : ControllerBase
{
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
            ActiveFederationProviders = await db.FederationProviders.CountAsync(x => x.IsActive, ct),
            ExpiringProvisioningTokens = await db.ProvisioningTokens.CountAsync(x => x.RevokedAt == null && x.ExpiresAt > now && x.ExpiresAt <= soon, ct),
            UnverifiedEventHooks = await db.EventHooks.CountAsync(x => x.IsActive && !x.IsVerified, ct),
            DeadLetterDeliveries = await db.EventHookDeliveries.CountAsync(x => x.DeadLetteredAt != null, ct),
            FailedLoginsLast24Hours = await db.AuditLogs.CountAsync(x => x.CreatedAt >= since && (x.Action.Contains("LOGIN_FAILED") || x.Action == "LOGIN_INVALID"), ct),
            HighRiskObservationsLast24Hours = await db.AuthenticationObservations.CountAsync(x => x.ObservedAt >= since && (x.RiskLevel == AccessRiskLevel.High || x.RiskLevel == AccessRiskLevel.Critical), ct)
        };
        return Ok(ApiResponse<object>.Ok(dto));
    }
}
