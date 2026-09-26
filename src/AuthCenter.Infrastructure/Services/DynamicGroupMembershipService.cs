using AuthCenter.Application.Interfaces;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

/// <summary>
/// Keeps rule-managed groups in step with the universal profile. A group with active rules is
/// managed by them: a user belongs to it when any of its rules matches (rules on different
/// attributes are alternatives). Losing a membership ends the user's sessions, as a manual removal
/// does; a new one needs no sign-out, since every token renewal reads the current entitlements. A
/// group whose rules are all removed or deactivated keeps its members and becomes manual.
/// </summary>
public sealed class DynamicGroupMembershipService(AuthCenterDbContext db, IRefreshTokenService refreshTokens, IDateTimeProvider clock)
{
    /// <summary>Re-evaluates every rule-managed group for one user after their profile changed. The caller saves.</summary>
    public async Task<int> SynchronizeUserAsync(Guid userId, CancellationToken ct = default)
    {
        var rules = await ActiveRules().ToListAsync(ct);
        if (rules.Count == 0)
            return 0;
        var values = await db.UserProfileAttributeValues.AsNoTracking()
            .Where(value => value.UserId == userId)
            .ToDictionaryAsync(value => value.AttributeDefinitionId, value => value.ValueJson, ct);

        var changes = 0;
        var lostAccess = false;
        foreach (var group in rules.GroupBy(rule => rule.DirectoryGroupId))
        {
            var matches = group.Any(rule => GroupRuleEvaluator.Matches(rule, rule.ProfileAttributeDefinition.DataType, values.GetValueOrDefault(rule.ProfileAttributeDefinitionId)));
            var membership = await db.UserGroupMemberships.FindAsync([group.Key, userId], ct);
            if (matches && membership is null)
            {
                db.UserGroupMemberships.Add(new UserGroupMembership { GroupId = group.Key, UserId = userId, CreatedAt = clock.UtcNow });
                changes++;
            }
            else if (!matches && membership is not null)
            {
                db.UserGroupMemberships.Remove(membership);
                changes++;
                lostAccess = true;
            }
        }
        if (lostAccess)
            await refreshTokens.RevokeAllForUserAsync(userId, ct);
        return changes;
    }

    /// <summary>Re-evaluates one group for the whole directory after its rules changed, and saves.</summary>
    public async Task<(int Added, int Removed)> SynchronizeGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        var rules = await ActiveRules().Where(rule => rule.DirectoryGroupId == groupId).ToListAsync(ct);
        if (rules.Count == 0)
            return (0, 0);

        var definitionIds = rules.Select(rule => rule.ProfileAttributeDefinitionId).Distinct().ToList();
        var values = await db.UserProfileAttributeValues.AsNoTracking()
            .Where(value => definitionIds.Contains(value.AttributeDefinitionId) && value.User.DeletedAt == null)
            .Select(value => new { value.UserId, value.AttributeDefinitionId, value.ValueJson })
            .ToListAsync(ct);
        var matching = values
            .GroupBy(value => value.UserId)
            .Where(user => rules.Any(rule => GroupRuleEvaluator.Matches(rule, rule.ProfileAttributeDefinition.DataType, user.FirstOrDefault(value => value.AttributeDefinitionId == rule.ProfileAttributeDefinitionId)?.ValueJson)))
            .Select(user => user.Key)
            .ToHashSet();
        var memberships = await db.UserGroupMemberships.Where(membership => membership.GroupId == groupId).ToListAsync(ct);
        var members = memberships.Select(membership => membership.UserId).ToHashSet();

        var removed = memberships.Where(membership => !matching.Contains(membership.UserId)).ToList();
        db.UserGroupMemberships.RemoveRange(removed);
        var added = matching.Where(userId => !members.Contains(userId)).ToList();
        foreach (var userId in added)
            db.UserGroupMemberships.Add(new UserGroupMembership { GroupId = groupId, UserId = userId, CreatedAt = clock.UtcNow });
        await db.SaveChangesAsync(ct);

        foreach (var membership in removed)
            await refreshTokens.RevokeAllForUserAsync(membership.UserId, ct);
        return (added.Count, removed.Count);
    }

    /// <summary>Whether active rules decide the group's members, so they cannot be added or removed by hand.</summary>
    public Task<bool> IsRuleManagedAsync(Guid groupId, CancellationToken ct = default) =>
        ActiveRules().AnyAsync(rule => rule.DirectoryGroupId == groupId, ct);

    /// <summary>The groups, among these, whose members follow active rules.</summary>
    public async Task<HashSet<Guid>> RuleManagedAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken ct = default) =>
        (await ActiveRules().Where(rule => groupIds.Contains(rule.DirectoryGroupId)).Select(rule => rule.DirectoryGroupId).Distinct().ToListAsync(ct)).ToHashSet();

    private IQueryable<DynamicGroupRule> ActiveRules() => db.DynamicGroupRules.AsNoTracking()
        .Include(rule => rule.ProfileAttributeDefinition)
        .Where(rule => rule.IsActive && rule.DirectoryGroup.IsActive && rule.ProfileAttributeDefinition.IsActive);
}
