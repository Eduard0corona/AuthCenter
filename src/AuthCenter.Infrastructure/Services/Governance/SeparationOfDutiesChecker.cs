using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services.Governance;

/// <summary>
/// Separation of duties against the roles users hold now: directly, or through an active group
/// that has the role and its application. A change is refused when it gives someone both roles of
/// an active rule they did not already hold together.
/// </summary>
public sealed class SeparationOfDutiesChecker(AuthCenterDbContext db, IAuditService audit, ICurrentUserService currentUser) : ISeparationOfDutiesChecker
{
    public async Task<SeparationOfDutiesConflict?> CheckUserAsync(Guid userId, IReadOnlyCollection<Guid> addedRoleIds, IReadOnlyCollection<Guid>? removedDirectRoleIds = null, CancellationToken ct = default)
    {
        var added = addedRoleIds.ToHashSet();
        var rules = await RulesInvolvingAsync(added, ct);
        if (rules.Count == 0)
            return null;

        var direct = await db.UserRoles.Where(userRole => userRole.UserId == userId).Select(userRole => userRole.RoleId).ToListAsync(ct);
        var viaGroups = await GroupRoles(db, userId).ToListAsync(ct);
        var before = direct.Concat(viaGroups).ToHashSet();
        var after = direct.Except(removedDirectRoleIds ?? []).Concat(added).Concat(viaGroups).ToHashSet();
        var broken = rules.FirstOrDefault(rule =>
            after.Contains(rule.FirstRoleId) && after.Contains(rule.SecondRoleId) &&
            !(before.Contains(rule.FirstRoleId) && before.Contains(rule.SecondRoleId)));
        return broken is null ? null : await ConflictAsync(broken, userId, ct);
    }

    public async Task<SeparationOfDutiesConflict?> CheckGroupAsync(Guid groupId, IReadOnlyCollection<Guid> addedRoleIds, IReadOnlyCollection<Guid>? removedRoleIds = null, CancellationToken ct = default)
    {
        var added = addedRoleIds.ToHashSet();
        var removed = removedRoleIds ?? [];
        var rules = await RulesInvolvingAsync(added, ct);
        var members = db.UserGroupMemberships.Where(membership => membership.GroupId == groupId).Select(membership => membership.UserId);
        foreach (var rule in rules)
        {
            var firstAdded = added.Contains(rule.FirstRoleId);
            var secondAdded = added.Contains(rule.SecondRoleId);
            var firstBefore = HoldersOf(db, rule.FirstRoleId);
            var secondBefore = HoldersOf(db, rule.SecondRoleId);
            IQueryable<Guid> offenders;
            if (firstAdded && secondAdded)
            {
                offenders = members.Where(userId => !(firstBefore.Contains(userId) && secondBefore.Contains(userId)));
            }
            else
            {
                var (addedRole, otherRole) = firstAdded ? (rule.FirstRoleId, rule.SecondRoleId) : (rule.SecondRoleId, rule.FirstRoleId);
                // A role the change takes from this group no longer counts through it.
                var otherAfter = HoldersOf(db, otherRole, removed.Contains(otherRole) ? groupId : null);
                var addedBefore = HoldersOf(db, addedRole);
                offenders = members.Where(userId => otherAfter.Contains(userId) && !addedBefore.Contains(userId));
            }
            var offender = await offenders.Select(userId => (Guid?)userId).FirstOrDefaultAsync(ct);
            if (offender is { } userId)
                return await ConflictAsync(rule, userId, ct);
        }
        return null;
    }

    public async Task<SeparationOfDutiesConflict?> CheckMembershipAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        var groupRoles = await db.GroupRoleAssignments
            .Where(assignment => assignment.GroupId == groupId && assignment.Group.IsActive && assignment.Role.IsActive &&
                assignment.Group.ApplicationAssignments.Any(application => application.ApplicationSystemId == assignment.Role.ApplicationSystemId))
            .Select(assignment => assignment.RoleId)
            .ToListAsync(ct);
        return groupRoles.Count == 0 ? null : await CheckUserAsync(userId, groupRoles, null, ct);
    }

    /// <summary>Users who hold the role, directly or through an active group (optionally leaving one group out).</summary>
    internal static IQueryable<Guid> HoldersOf(AuthCenterDbContext db, Guid roleId, Guid? excludedGroupId = null)
    {
        var direct = db.UserRoles.Where(userRole => userRole.RoleId == roleId).Select(userRole => userRole.UserId);
        var groups = db.GroupRoleAssignments
            .Where(assignment => assignment.RoleId == roleId && assignment.Role.IsActive && assignment.Group.IsActive &&
                (excludedGroupId == null || assignment.GroupId != excludedGroupId) &&
                assignment.Group.ApplicationAssignments.Any(application => application.ApplicationSystemId == assignment.Role.ApplicationSystemId))
            .Select(assignment => assignment.GroupId);
        var viaGroups = db.UserGroupMemberships.Where(membership => groups.Contains(membership.GroupId)).Select(membership => membership.UserId);
        return direct.Union(viaGroups);
    }

    /// <summary>The roles the user holds through active groups.</summary>
    internal static IQueryable<Guid> GroupRoles(AuthCenterDbContext db, Guid userId) => db.UserGroupMemberships
        .Where(membership => membership.UserId == userId && membership.Group.IsActive)
        .SelectMany(membership => membership.Group.RoleAssignments)
        .Where(assignment => assignment.Role.IsActive &&
            assignment.Group.ApplicationAssignments.Any(application => application.ApplicationSystemId == assignment.Role.ApplicationSystemId))
        .Select(assignment => assignment.RoleId);

    private async Task<List<RuleRow>> RulesInvolvingAsync(IReadOnlySet<Guid> roleIds, CancellationToken ct) =>
        roleIds.Count == 0
            ? []
            : await db.SeparationOfDutiesRules.AsNoTracking()
                .Where(rule => rule.IsActive && rule.FirstRole.IsActive && rule.SecondRole.IsActive &&
                    (roleIds.Contains(rule.FirstRoleId) || roleIds.Contains(rule.SecondRoleId)))
                .OrderBy(rule => rule.Name)
                .Select(rule => new RuleRow(rule.Id, rule.Name, rule.FirstRoleId, rule.SecondRoleId, rule.FirstRole.Name ?? rule.FirstRole.DisplayName, rule.SecondRole.Name ?? rule.SecondRole.DisplayName))
                .ToListAsync(ct);

    /// <summary>The conflict, recorded in the System Log (on its own connection: the refused change is rolled back).</summary>
    private async Task<SeparationOfDutiesConflict> ConflictAsync(RuleRow rule, Guid userId, CancellationToken ct)
    {
        var email = await db.Users.IgnoreQueryFilters().Where(user => user.Id == userId).Select(user => user.Email).SingleOrDefaultAsync(ct);
        var conflict = new SeparationOfDutiesConflict(rule.Id, rule.Name, userId, email ?? userId.ToString(), rule.FirstRoleName, rule.SecondRoleName);
        await audit.LogAsync("SOD_CONFLICT_BLOCKED", currentUser.UserId, DomainConstants.SystemCodes.AuthCenter, nameof(ApplicationUser), userId.ToString(),
            metadata: new { ruleId = rule.Id, rule = rule.Name, firstRole = rule.FirstRoleName, secondRole = rule.SecondRoleName }, ct: ct);
        return conflict;
    }

    private sealed record RuleRow(Guid Id, string Name, Guid FirstRoleId, Guid SecondRoleId, string FirstRoleName, string SecondRoleName);
}
