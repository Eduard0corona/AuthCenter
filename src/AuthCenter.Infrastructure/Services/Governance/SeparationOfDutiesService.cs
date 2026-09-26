using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Governance;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Governance;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services.Governance;

/// <summary>Separation of duties rules, and the active users who hold both roles of an active rule.</summary>
public sealed class SeparationOfDutiesService(AuthCenterDbContext db, IDateTimeProvider clock, ICurrentUserService currentUser) : ISeparationOfDutiesService
{
    public async Task<PagedResult<SeparationOfDutiesRuleDto>> GetRulesAsync(SeparationOfDutiesRuleQuery query, CancellationToken ct = default)
    {
        var rules = db.SeparationOfDutiesRules.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            rules = rules.Where(rule => rule.Name.Contains(search) || rule.FirstRole.DisplayName.Contains(search) || rule.SecondRole.DisplayName.Contains(search));
        }
        if (query.IsActive is { } active)
            rules = rules.Where(rule => rule.IsActive == active);
        var total = await rules.CountAsync(ct);
        var page = await Project(rules.OrderBy(rule => rule.Name).Skip(query.Skip).Take(query.PageSize)).ToListAsync(ct);
        return PagedResult<SeparationOfDutiesRuleDto>.Create(await WithViolationCountsAsync(page, ct), total, query.Page, query.PageSize);
    }

    public async Task<SeparationOfDutiesRuleDto?> GetRuleAsync(Guid ruleId, CancellationToken ct = default)
    {
        var rule = await Project(db.SeparationOfDutiesRules.AsNoTracking().Where(item => item.Id == ruleId)).SingleOrDefaultAsync(ct);
        return rule is null ? null : (await WithViolationCountsAsync([rule], ct))[0];
    }

    public async Task<OperationResult<SeparationOfDutiesRuleDto>> CreateRuleAsync(SeparationOfDutiesRuleRequest request, CancellationToken ct = default)
    {
        var invalid = await ValidateAsync(null, request, ct);
        if (invalid is not null)
            return invalid;
        var rule = new SeparationOfDutiesRule
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = Text(request.Description),
            FirstRoleId = request.FirstRoleId,
            SecondRoleId = request.SecondRoleId,
            IsActive = request.IsActive,
            CreatedAt = clock.UtcNow
        };
        db.SeparationOfDutiesRules.Add(rule);
        AddAudit("SOD_RULE_CREATED", rule);
        await db.SaveChangesAsync(ct);
        return OperationResult<SeparationOfDutiesRuleDto>.Success((await GetRuleAsync(rule.Id, ct))!);
    }

    public async Task<OperationResult<SeparationOfDutiesRuleDto>> UpdateRuleAsync(Guid ruleId, SeparationOfDutiesRuleRequest request, CancellationToken ct = default)
    {
        var rule = await db.SeparationOfDutiesRules.SingleOrDefaultAsync(item => item.Id == ruleId, ct);
        if (rule is null)
            return OperationResult<SeparationOfDutiesRuleDto>.Failure("SOD_RULE_NOT_FOUND", "Separation of duties rule not found.");
        var invalid = await ValidateAsync(ruleId, request, ct);
        if (invalid is not null)
            return invalid;
        if (!rule.TryAdvance(request.Version))
            return OperationResult<SeparationOfDutiesRuleDto>.Failure(VersionedUpdates.ConflictCode, "The rule changed after it was loaded. Reload it and try again.");
        rule.Name = request.Name.Trim();
        rule.Description = Text(request.Description);
        rule.FirstRoleId = request.FirstRoleId;
        rule.SecondRoleId = request.SecondRoleId;
        rule.IsActive = request.IsActive;
        rule.UpdatedAt = clock.UtcNow;
        AddAudit("SOD_RULE_UPDATED", rule);
        await db.SaveChangesAsync(ct);
        return OperationResult<SeparationOfDutiesRuleDto>.Success((await GetRuleAsync(rule.Id, ct))!);
    }

    public async Task<OperationResult> DeleteRuleAsync(Guid ruleId, CancellationToken ct = default)
    {
        var rule = await db.SeparationOfDutiesRules.SingleOrDefaultAsync(item => item.Id == ruleId, ct);
        if (rule is null)
            return OperationResult.Failure("SOD_RULE_NOT_FOUND", "Separation of duties rule not found.");
        db.SeparationOfDutiesRules.Remove(rule);
        AddAudit("SOD_RULE_DELETED", rule);
        await db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<PagedResult<SeparationOfDutiesViolationDto>> GetViolationsAsync(SeparationOfDutiesViolationQuery query, CancellationToken ct = default)
    {
        var rules = await EnforcedRulesAsync(query.RuleId, ct);
        var counts = new List<int>(rules.Count);
        foreach (var rule in rules)
            counts.Add(await Violators(rule).CountAsync(ct));

        var items = new List<SeparationOfDutiesViolationDto>();
        var skip = query.Skip;
        var take = query.PageSize;
        for (var index = 0; index < rules.Count && take > 0; index++)
        {
            if (skip >= counts[index])
            {
                skip -= counts[index];
                continue;
            }
            var rule = rules[index];
            var users = await Violators(rule)
                .OrderBy(user => user.Email)
                .Skip(skip)
                .Take(take)
                .Select(user => new GovernanceUserDto { Id = user.Id, FullName = user.FullName, Email = user.Email ?? string.Empty, IsActive = user.IsActive })
                .ToListAsync(ct);
            skip = 0;
            take -= users.Count;
            foreach (var user in users)
            {
                items.Add(new SeparationOfDutiesViolationDto
                {
                    RuleId = rule.Id,
                    RuleName = rule.Name,
                    User = user,
                    FirstRole = await HoldingAsync(user.Id, rule.FirstRoleId, ct),
                    SecondRole = await HoldingAsync(user.Id, rule.SecondRoleId, ct)
                });
            }
        }
        return PagedResult<SeparationOfDutiesViolationDto>.Create(items, counts.Sum(), query.Page, query.PageSize);
    }

    public async Task<int> CountViolationsAsync(CancellationToken ct = default)
    {
        var total = 0;
        foreach (var rule in await EnforcedRulesAsync(null, ct))
            total += await Violators(rule).CountAsync(ct);
        return total;
    }

    private async Task<OperationResult<SeparationOfDutiesRuleDto>?> ValidateAsync(Guid? ruleId, SeparationOfDutiesRuleRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 150)
            return Invalid("The name is required and at most 150 characters.");
        if (request.Description is { Length: > 1000 })
            return Invalid("The description is at most 1000 characters.");
        if (request.FirstRoleId == Guid.Empty || request.SecondRoleId == Guid.Empty || request.FirstRoleId == request.SecondRoleId)
            return Invalid("Choose two different roles.");
        var roles = await db.Roles.CountAsync(role => role.Id == request.FirstRoleId || role.Id == request.SecondRoleId, ct);
        if (roles != 2)
            return OperationResult<SeparationOfDutiesRuleDto>.Failure("ROLE_NOT_FOUND", "One or both roles were not found.");
        if (await db.SeparationOfDutiesRules.AnyAsync(rule => rule.Id != ruleId && rule.Name == name, ct))
            return OperationResult<SeparationOfDutiesRuleDto>.Failure("SOD_RULE_EXISTS", "Another rule has this name.");
        if (await db.SeparationOfDutiesRules.AnyAsync(rule => rule.Id != ruleId &&
                ((rule.FirstRoleId == request.FirstRoleId && rule.SecondRoleId == request.SecondRoleId) ||
                 (rule.FirstRoleId == request.SecondRoleId && rule.SecondRoleId == request.FirstRoleId)), ct))
            return OperationResult<SeparationOfDutiesRuleDto>.Failure("SOD_RULE_EXISTS", "Another rule already keeps these two roles apart.");
        return null;
    }

    private async Task<List<RuleRow>> EnforcedRulesAsync(Guid? ruleId, CancellationToken ct) =>
        await db.SeparationOfDutiesRules.AsNoTracking()
            .Where(rule => rule.IsActive && rule.FirstRole.IsActive && rule.SecondRole.IsActive && (ruleId == null || rule.Id == ruleId))
            .OrderBy(rule => rule.Name)
            .Select(rule => new RuleRow(rule.Id, rule.Name, rule.FirstRoleId, rule.SecondRoleId))
            .ToListAsync(ct);

    /// <summary>Active, not deleted users who hold both roles of the rule.</summary>
    private IQueryable<ApplicationUser> Violators(RuleRow rule)
    {
        var first = SeparationOfDutiesChecker.HoldersOf(db, rule.FirstRoleId);
        var second = SeparationOfDutiesChecker.HoldersOf(db, rule.SecondRoleId);
        return db.Users.AsNoTracking().Where(user => user.IsActive && first.Contains(user.Id) && second.Contains(user.Id));
    }

    private async Task<SeparationOfDutiesHoldingDto> HoldingAsync(Guid userId, Guid roleId, CancellationToken ct)
    {
        var role = await db.Roles.AsNoTracking().Where(item => item.Id == roleId)
            .Select(item => new { item.DisplayName, item.ApplicationSystemId, ApplicationCode = item.ApplicationSystem != null ? item.ApplicationSystem.Code : null })
            .SingleAsync(ct);
        var groups = await db.UserGroupMemberships.AsNoTracking()
            .Where(membership => membership.UserId == userId && membership.Group.IsActive &&
                membership.Group.RoleAssignments.Any(assignment => assignment.RoleId == roleId) &&
                membership.Group.ApplicationAssignments.Any(application => application.ApplicationSystemId == role.ApplicationSystemId))
            .OrderBy(membership => membership.Group.Name)
            .Select(membership => membership.Group.Name)
            .ToListAsync(ct);
        return new SeparationOfDutiesHoldingDto
        {
            RoleId = roleId,
            RoleName = role.DisplayName,
            ApplicationCode = role.ApplicationCode,
            Direct = await db.UserRoles.AnyAsync(item => item.UserId == userId && item.RoleId == roleId, ct),
            Groups = groups
        };
    }

    private async Task<List<SeparationOfDutiesRuleDto>> WithViolationCountsAsync(List<SeparationOfDutiesRuleDto> rules, CancellationToken ct)
    {
        var result = new List<SeparationOfDutiesRuleDto>(rules.Count);
        foreach (var rule in rules)
        {
            var enforced = rule.IsActive && rule.FirstRole.IsActive && rule.SecondRole.IsActive;
            var count = enforced ? await Violators(new RuleRow(rule.Id, rule.Name, rule.FirstRole.Id, rule.SecondRole.Id)).CountAsync(ct) : 0;
            result.Add(new SeparationOfDutiesRuleDto
            {
                Id = rule.Id,
                Name = rule.Name,
                Description = rule.Description,
                FirstRole = rule.FirstRole,
                SecondRole = rule.SecondRole,
                IsActive = rule.IsActive,
                ViolationCount = count,
                CreatedAt = rule.CreatedAt,
                UpdatedAt = rule.UpdatedAt,
                Version = rule.Version
            });
        }
        return result;
    }

    private static IQueryable<SeparationOfDutiesRuleDto> Project(IQueryable<SeparationOfDutiesRule> rules) => rules.Select(rule => new SeparationOfDutiesRuleDto
    {
        Id = rule.Id,
        Name = rule.Name,
        Description = rule.Description,
        FirstRole = new SeparationOfDutiesRoleDto
        {
            Id = rule.FirstRoleId,
            Name = rule.FirstRole.DisplayName,
            ApplicationSystemId = rule.FirstRole.ApplicationSystemId,
            ApplicationCode = rule.FirstRole.ApplicationSystem != null ? rule.FirstRole.ApplicationSystem.Code : null,
            IsActive = rule.FirstRole.IsActive
        },
        SecondRole = new SeparationOfDutiesRoleDto
        {
            Id = rule.SecondRoleId,
            Name = rule.SecondRole.DisplayName,
            ApplicationSystemId = rule.SecondRole.ApplicationSystemId,
            ApplicationCode = rule.SecondRole.ApplicationSystem != null ? rule.SecondRole.ApplicationSystem.Code : null,
            IsActive = rule.SecondRole.IsActive
        },
        IsActive = rule.IsActive,
        CreatedAt = rule.CreatedAt,
        UpdatedAt = rule.UpdatedAt,
        Version = rule.Version
    });

    private void AddAudit(string action, SeparationOfDutiesRule rule) => db.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(),
        UserId = currentUser.UserId,
        ApplicationCode = DomainConstants.SystemCodes.AuthCenter,
        Action = action,
        EntityName = nameof(SeparationOfDutiesRule),
        EntityId = rule.Id.ToString(),
        MetadataJson = JsonSerializer.Serialize(new { rule.Name, rule.FirstRoleId, rule.SecondRoleId, rule.IsActive }),
        TraceId = System.Diagnostics.Activity.Current?.TraceId.ToHexString(),
        CreatedAt = clock.UtcNow
    });

    private static OperationResult<SeparationOfDutiesRuleDto> Invalid(string message) =>
        OperationResult<SeparationOfDutiesRuleDto>.Failure("SOD_RULE_INVALID", message);

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record RuleRow(Guid Id, string Name, Guid FirstRoleId, Guid SecondRoleId);
}
