using System.Data;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Groups;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Groups;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public class DirectoryGroupService : IDirectoryGroupService
{
    private readonly AuthCenterDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUserService _currentUser;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly DynamicGroupMembershipService _dynamicGroups;

    public DirectoryGroupService(
        AuthCenterDbContext db,
        IDateTimeProvider clock,
        ICurrentUserService currentUser,
        IRefreshTokenService refreshTokens,
        DynamicGroupMembershipService dynamicGroups)
    {
        _db = db;
        _clock = clock;
        _currentUser = currentUser;
        _refreshTokens = refreshTokens;
        _dynamicGroups = dynamicGroups;
    }

    public async Task<PagedResult<DirectoryGroupDto>> GetAllAsync(DirectoryGroupQuery query, CancellationToken ct = default)
    {
        var groups = BaseQuery();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = Normalize(query.Search);
            groups = groups.Where(group => group.NormalizedName.StartsWith(normalized));
        }

        if (query.IsActive.HasValue)
            groups = groups.Where(group => group.IsActive == query.IsActive.Value);

        var totalCount = await groups.CountAsync(ct);
        var items = await groups
            .OrderBy(group => group.Name)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(ct);

        var ruleManaged = await _dynamicGroups.RuleManagedAsync(items.Select(group => group.Id).ToList(), ct);
        return PagedResult<DirectoryGroupDto>.Create(
            items.Select(group => Map(group, ruleManaged.Contains(group.Id))).ToList(), totalCount, query.Page, query.PageSize);
    }

    public async Task<DirectoryGroupDto?> GetByIdAsync(Guid groupId, CancellationToken ct = default)
    {
        var group = await BaseQuery().FirstOrDefaultAsync(candidate => candidate.Id == groupId, ct);
        return group is null ? null : Map(group, await _dynamicGroups.IsRuleManagedAsync(group.Id, ct));
    }

    public async Task<PagedResult<DirectoryGroupMemberDto>?> GetMembersAsync(
        Guid groupId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (!await _db.DirectoryGroups.AnyAsync(group => group.Id == groupId, ct))
            return null;

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.UserGroupMemberships
            .AsNoTracking()
            .Where(membership => membership.GroupId == groupId)
            .OrderBy(membership => membership.User.FullName);
        var totalCount = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(membership => new DirectoryGroupMemberDto
            {
                UserId = membership.UserId,
                FullName = membership.User.FullName,
                Email = membership.User.Email ?? string.Empty,
                IsActive = membership.User.IsActive,
                AddedAt = membership.CreatedAt
            })
            .ToListAsync(ct);

        return PagedResult<DirectoryGroupMemberDto>.Create(items, totalCount, page, pageSize);
    }

    public async Task<OperationResult<DirectoryGroupDto>> CreateAsync(
        CreateDirectoryGroupRequest request,
        CancellationToken ct = default)
    {
        var normalizedName = Normalize(request.Name);
        if (await _db.DirectoryGroups.AnyAsync(group => group.NormalizedName == normalizedName, ct))
            return OperationResult<DirectoryGroupDto>.Failure("GROUP_EXISTS", "A group with this name already exists.");

        var group = new DirectoryGroup
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            NormalizedName = normalizedName,
            Description = NormalizeOptional(request.Description),
            IsActive = true,
            CreatedAt = _clock.UtcNow
        };
        _db.DirectoryGroups.Add(group);
        AddAudit("DIRECTORY_GROUP_CREATED", group);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return OperationResult<DirectoryGroupDto>.Failure("GROUP_EXISTS", "A group with this name already exists.");
        }

        return OperationResult<DirectoryGroupDto>.Success(Map(group, isRuleManaged: false));
    }

    public async Task<OperationResult<DirectoryGroupDto>> UpdateAsync(
        Guid groupId,
        UpdateDirectoryGroupRequest request,
        CancellationToken ct = default)
    {
        var group = await BaseQuery(tracking: true).FirstOrDefaultAsync(candidate => candidate.Id == groupId, ct);
        if (group is null)
            return OperationResult<DirectoryGroupDto>.Failure("GROUP_NOT_FOUND", "Group not found.");

        var normalizedName = Normalize(request.Name);
        if (await _db.DirectoryGroups.AnyAsync(candidate => candidate.Id != groupId && candidate.NormalizedName == normalizedName, ct))
            return OperationResult<DirectoryGroupDto>.Failure("GROUP_EXISTS", "A group with this name already exists.");
        if (!group.TryAdvance(request.Version))
            return OperationResult<DirectoryGroupDto>.Failure(VersionedUpdates.ConflictCode, "The group changed after it was loaded.");

        group.Name = request.Name.Trim();
        group.NormalizedName = normalizedName;
        group.Description = NormalizeOptional(request.Description);
        group.UpdatedAt = _clock.UtcNow;
        AddAudit("DIRECTORY_GROUP_UPDATED", group);
        await _db.SaveChangesAsync(ct);
        return OperationResult<DirectoryGroupDto>.Success(Map(group, await _dynamicGroups.IsRuleManagedAsync(group.Id, ct)));
    }

    public async Task<OperationResult> ActivateAsync(Guid groupId, CancellationToken ct = default) =>
        await SetActiveAsync(groupId, true, ct);

    public async Task<OperationResult> DeactivateAsync(Guid groupId, CancellationToken ct = default) =>
        await SetActiveAsync(groupId, false, ct);

    public async Task<OperationResult> AddMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        var group = await _db.DirectoryGroups.FindAsync([groupId], ct);
        if (group is null)
            return OperationResult.Failure("GROUP_NOT_FOUND", "Group not found.");
        if (!group.IsActive)
            return OperationResult.Failure("GROUP_INACTIVE", "Members cannot be added to an inactive group.");
        if (await _dynamicGroups.IsRuleManagedAsync(groupId, ct))
            return RuleManaged();
        if (!await _db.Users.AnyAsync(user => user.Id == userId, ct))
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");
        if (await _db.UserGroupMemberships.AnyAsync(membership => membership.GroupId == groupId && membership.UserId == userId, ct))
            return OperationResult.Failure("MEMBER_EXISTS", "User is already a member of this group.");

        await _refreshTokens.RevokeAllForUserAsync(userId, ct);
        _db.UserGroupMemberships.Add(new UserGroupMembership
        {
            GroupId = groupId,
            UserId = userId,
            CreatedAt = _clock.UtcNow
        });
        AddAudit("DIRECTORY_GROUP_MEMBER_ADDED", group, userId: userId);
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        var membership = await _db.UserGroupMemberships
            .Include(item => item.Group)
            .FirstOrDefaultAsync(item => item.GroupId == groupId && item.UserId == userId, ct);
        if (membership is null)
            return OperationResult.Failure("MEMBER_NOT_FOUND", "User is not a member of this group.");
        if (await _dynamicGroups.IsRuleManagedAsync(groupId, ct))
            return RuleManaged();

        await _refreshTokens.RevokeAllForUserAsync(userId, ct);
        _db.UserGroupMemberships.Remove(membership);
        AddAudit("DIRECTORY_GROUP_MEMBER_REMOVED", membership.Group, userId: userId);
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> AssignApplicationAsync(
        Guid groupId,
        Guid applicationSystemId,
        CancellationToken ct = default)
    {
        var group = await _db.DirectoryGroups.FindAsync([groupId], ct);
        if (group is null)
            return OperationResult.Failure("GROUP_NOT_FOUND", "Group not found.");
        if (!group.IsActive)
            return OperationResult.Failure("GROUP_INACTIVE", "Applications cannot be assigned to an inactive group.");
        var application = await _db.ApplicationSystems.FindAsync([applicationSystemId], ct);
        if (application is null)
            return OperationResult.Failure("APP_NOT_FOUND", "Application not found.");
        if (!application.IsActive)
            return OperationResult.Failure("APP_INACTIVE", "Inactive applications cannot be assigned.");
        if (await _db.GroupApplicationAssignments.AnyAsync(
                assignment => assignment.GroupId == groupId && assignment.ApplicationSystemId == applicationSystemId, ct))
            return OperationResult.Failure("APP_ALREADY_ASSIGNED", "Application is already assigned to this group.");

        await RevokeMemberSessionsAsync(groupId, application.Code, ct);
        _db.GroupApplicationAssignments.Add(new GroupApplicationAssignment
        {
            GroupId = groupId,
            ApplicationSystemId = applicationSystemId,
            CreatedAt = _clock.UtcNow
        });
        AddAudit("DIRECTORY_GROUP_APPLICATION_ASSIGNED", group, applicationSystemId: applicationSystemId);
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveApplicationAsync(
        Guid groupId,
        Guid applicationSystemId,
        CancellationToken ct = default)
    {
        var assignment = await _db.GroupApplicationAssignments
            .Include(item => item.Group)
            .Include(item => item.ApplicationSystem)
            .FirstOrDefaultAsync(item => item.GroupId == groupId && item.ApplicationSystemId == applicationSystemId, ct);
        if (assignment is null)
            return OperationResult.Failure("APP_NOT_ASSIGNED", "Application is not assigned to this group.");

        await RevokeMemberSessionsAsync(groupId, assignment.ApplicationSystem.Code, ct);
        var roleAssignments = await _db.GroupRoleAssignments
            .Where(item => item.GroupId == groupId && item.Role.ApplicationSystemId == applicationSystemId)
            .ToListAsync(ct);
        _db.GroupRoleAssignments.RemoveRange(roleAssignments);
        _db.GroupApplicationAssignments.Remove(assignment);
        AddAudit("DIRECTORY_GROUP_APPLICATION_REMOVED", assignment.Group, applicationSystemId: applicationSystemId);
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> AssignRoleAsync(Guid groupId, Guid roleId, CancellationToken ct = default)
    {
        var group = await _db.DirectoryGroups.FindAsync([groupId], ct);
        if (group is null)
            return OperationResult.Failure("GROUP_NOT_FOUND", "Group not found.");
        if (!group.IsActive)
            return OperationResult.Failure("GROUP_INACTIVE", "Roles cannot be assigned to an inactive group.");
        var role = await _db.Roles.Include(item => item.ApplicationSystem).FirstOrDefaultAsync(item => item.Id == roleId, ct);
        if (role is null)
            return OperationResult.Failure("ROLE_NOT_FOUND", "Role not found.");
        if (!role.IsActive || !role.ApplicationSystemId.HasValue || role.ApplicationSystem is null)
            return OperationResult.Failure("ROLE_INVALID", "Only active application roles can be assigned to groups.");
        if (role.IsSystemRole && role.DisplayName == DomainConstants.Roles.SuperAdmin)
            return OperationResult.Failure("SUPER_ADMIN_GROUP_ASSIGNMENT_FORBIDDEN", "SuperAdmin must be assigned directly to a named user.");
        if (!await _db.GroupApplicationAssignments.AnyAsync(
                assignment => assignment.GroupId == groupId && assignment.ApplicationSystemId == role.ApplicationSystemId.Value, ct))
            return OperationResult.Failure("GROUP_APP_ACCESS_REQUIRED", "Assign the role's application to the group first.");
        if (await _db.GroupRoleAssignments.AnyAsync(assignment => assignment.GroupId == groupId && assignment.RoleId == roleId, ct))
            return OperationResult.Failure("ROLE_ALREADY_ASSIGNED", "Role is already assigned to this group.");

        await RevokeMemberSessionsAsync(groupId, role.ApplicationSystem.Code, ct);
        _db.GroupRoleAssignments.Add(new GroupRoleAssignment
        {
            GroupId = groupId,
            RoleId = roleId,
            CreatedAt = _clock.UtcNow
        });
        AddAudit("DIRECTORY_GROUP_ROLE_ASSIGNED", group, roleId: roleId);
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveRoleAsync(Guid groupId, Guid roleId, CancellationToken ct = default)
    {
        var assignment = await _db.GroupRoleAssignments
            .Include(item => item.Group)
            .Include(item => item.Role)
                .ThenInclude(role => role.ApplicationSystem)
            .FirstOrDefaultAsync(item => item.GroupId == groupId && item.RoleId == roleId, ct);
        if (assignment is null)
            return OperationResult.Failure("ROLE_NOT_ASSIGNED", "Role is not assigned to this group.");

        if (assignment.Role.ApplicationSystem is not null)
            await RevokeMemberSessionsAsync(groupId, assignment.Role.ApplicationSystem.Code, ct);
        _db.GroupRoleAssignments.Remove(assignment);
        AddAudit("DIRECTORY_GROUP_ROLE_REMOVED", assignment.Group, roleId: roleId);
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public Task<OperationResult<DirectoryGroupDto>> SetAccessAsync(
        Guid groupId,
        SetDirectoryGroupAccessRequest request,
        CancellationToken ct = default) =>
        _db.RunRetriableAsync(() => SetAccessCoreAsync(groupId, request, ct));

    private async Task<OperationResult<DirectoryGroupDto>> SetAccessCoreAsync(
        Guid groupId,
        SetDirectoryGroupAccessRequest request,
        CancellationToken ct = default)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        var group = await BaseQuery(tracking: true).FirstOrDefaultAsync(candidate => candidate.Id == groupId, ct);
        if (group is null)
            return OperationResult<DirectoryGroupDto>.Failure("GROUP_NOT_FOUND", "Group not found.");
        if (!group.IsActive)
            return OperationResult<DirectoryGroupDto>.Failure("GROUP_INACTIVE", "Access cannot be changed for an inactive group.");

        var applicationIds = request.ApplicationSystemIds.Distinct().ToHashSet();
        var roleIds = request.RoleIds.Distinct().ToHashSet();
        var applications = await _db.ApplicationSystems
            .Where(application => applicationIds.Contains(application.Id))
            .ToListAsync(ct);
        if (applications.Count != applicationIds.Count)
            return OperationResult<DirectoryGroupDto>.Failure("APP_NOT_FOUND", "One or more applications were not found.");
        if (applications.Any(application => !application.IsActive))
            return OperationResult<DirectoryGroupDto>.Failure("APP_INACTIVE", "Inactive applications cannot be assigned.");

        var roles = await _db.Roles
            .Where(role => roleIds.Contains(role.Id))
            .ToListAsync(ct);
        if (roles.Count != roleIds.Count)
            return OperationResult<DirectoryGroupDto>.Failure("ROLE_NOT_FOUND", "One or more roles were not found.");
        if (roles.Any(role => !role.IsActive || !role.ApplicationSystemId.HasValue))
            return OperationResult<DirectoryGroupDto>.Failure("ROLE_INVALID", "Only active application roles can be assigned to groups.");
        if (roles.Any(role => role.IsSystemRole && role.DisplayName == DomainConstants.Roles.SuperAdmin))
            return OperationResult<DirectoryGroupDto>.Failure("SUPER_ADMIN_GROUP_ASSIGNMENT_FORBIDDEN", "SuperAdmin must be assigned directly to a named user.");
        if (roles.Any(role => !applicationIds.Contains(role.ApplicationSystemId!.Value)))
            return OperationResult<DirectoryGroupDto>.Failure("GROUP_APP_ACCESS_REQUIRED", "Every role requires its application to be assigned in the same operation.");

        var existingApplicationIds = group.ApplicationAssignments.Select(assignment => assignment.ApplicationSystemId).ToHashSet();
        var existingRoleIds = group.RoleAssignments.Select(assignment => assignment.RoleId).ToHashSet();
        var affectedApplicationCodes = group.ApplicationAssignments
            .Where(assignment => !applicationIds.Contains(assignment.ApplicationSystemId))
            .Select(assignment => assignment.ApplicationSystem.Code)
            .Concat(applications
                .Where(application => !existingApplicationIds.Contains(application.Id))
                .Select(application => application.Code))
            .Concat(roles
                .Where(role => !existingRoleIds.Contains(role.Id))
                .Join(applications, role => role.ApplicationSystemId, application => application.Id, (_, application) => application.Code))
            .Concat(group.RoleAssignments
                .Where(assignment => !roleIds.Contains(assignment.RoleId) && assignment.Role.ApplicationSystem is not null)
                .Select(assignment => assignment.Role.ApplicationSystem!.Code))
            .Distinct()
            .ToArray();

        foreach (var applicationCode in affectedApplicationCodes)
            await RevokeMemberSessionsAsync(groupId, applicationCode, ct);

        _db.GroupRoleAssignments.RemoveRange(group.RoleAssignments.Where(assignment => !roleIds.Contains(assignment.RoleId)));
        _db.GroupApplicationAssignments.RemoveRange(group.ApplicationAssignments.Where(assignment => !applicationIds.Contains(assignment.ApplicationSystemId)));
        _db.GroupApplicationAssignments.AddRange(applicationIds.Where(id => !existingApplicationIds.Contains(id)).Select(id => new GroupApplicationAssignment
        {
            GroupId = group.Id,
            ApplicationSystemId = id,
            CreatedAt = _clock.UtcNow
        }));
        _db.GroupRoleAssignments.AddRange(roleIds.Where(id => !existingRoleIds.Contains(id)).Select(id => new GroupRoleAssignment
        {
            GroupId = group.Id,
            RoleId = id,
            CreatedAt = _clock.UtcNow
        }));
        AddAudit("DIRECTORY_GROUP_ACCESS_REPLACED", group);
        await _db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        var updated = await BaseQuery().SingleAsync(candidate => candidate.Id == group.Id, ct);
        return OperationResult<DirectoryGroupDto>.Success(Map(updated, await _dynamicGroups.IsRuleManagedAsync(updated.Id, ct)));
    }

    private IQueryable<DirectoryGroup> BaseQuery(bool tracking = false)
    {
        var query = _db.DirectoryGroups
            .Include(group => group.Memberships)
            .Include(group => group.ApplicationAssignments)
                .ThenInclude(assignment => assignment.ApplicationSystem)
            .Include(group => group.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
                    .ThenInclude(role => role.ApplicationSystem)
            .AsSplitQuery();
        return tracking ? query : query.AsNoTracking();
    }

    private async Task<OperationResult> SetActiveAsync(Guid groupId, bool active, CancellationToken ct)
    {
        var group = await _db.DirectoryGroups.FindAsync([groupId], ct);
        if (group is null)
            return OperationResult.Failure("GROUP_NOT_FOUND", "Group not found.");
        if (group.IsActive == active)
            return OperationResult.Failure(active ? "GROUP_ALREADY_ACTIVE" : "GROUP_ALREADY_INACTIVE",
                active ? "Group is already active." : "Group is already inactive.");

        await RevokeMemberSessionsAsync(groupId, null, ct);
        group.IsActive = active;
        group.UpdatedAt = _clock.UtcNow;
        AddAudit(active ? "DIRECTORY_GROUP_ACTIVATED" : "DIRECTORY_GROUP_DEACTIVATED", group);
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    private async Task RevokeMemberSessionsAsync(Guid groupId, string? applicationCode, CancellationToken ct)
    {
        var tokens = _db.RefreshTokens.Where(token =>
            token.RevokedAt == null &&
            _db.UserGroupMemberships.Any(membership =>
                membership.GroupId == groupId && membership.UserId == token.UserId));
        if (applicationCode is not null)
            tokens = tokens.Where(token => token.ApplicationCode == applicationCode);

        var now = _clock.UtcNow;
        if (_db.Database.IsRelational())
        {
            await tokens.ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), ct);
            return;
        }

        var trackedTokens = await tokens.ToListAsync(ct);
        foreach (var token in trackedTokens)
            token.RevokedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    private void AddAudit(
        string action,
        DirectoryGroup group,
        Guid? userId = null,
        Guid? applicationSystemId = null,
        Guid? roleId = null)
    {
        var metadata = userId.HasValue || applicationSystemId.HasValue || roleId.HasValue
            ? JsonSerializer.Serialize(new { userId, applicationSystemId, roleId })
            : null;
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = _currentUser.UserId,
            ApplicationCode = "AUTHCENTER",
            Action = action,
            EntityName = nameof(DirectoryGroup),
            EntityId = group.Id.ToString(),
            MetadataJson = metadata,
            CreatedAt = _clock.UtcNow
        });
    }

    private static OperationResult RuleManaged() => OperationResult.Failure(
        "GROUP_MANAGED_BY_RULES", "The group's members follow its rules; change the rules or the users' profiles instead.");

    private static DirectoryGroupDto Map(DirectoryGroup group, bool isRuleManaged) => new()
    {
        Version = group.Version,
        IsRuleManaged = isRuleManaged,
        Id = group.Id,
        Name = group.Name,
        Description = group.Description,
        IsActive = group.IsActive,
        CreatedAt = group.CreatedAt,
        UpdatedAt = group.UpdatedAt,
        MemberCount = group.Memberships.Count,
        Applications = group.ApplicationAssignments
            .OrderBy(assignment => assignment.ApplicationSystem.Code)
            .Select(assignment => new DirectoryGroupApplicationDto
            {
                Id = assignment.ApplicationSystemId,
                Code = assignment.ApplicationSystem.Code,
                Name = assignment.ApplicationSystem.Name
            })
            .ToList(),
        Roles = group.RoleAssignments
            .Where(assignment => assignment.Role.ApplicationSystemId.HasValue && assignment.Role.ApplicationSystem is not null)
            .OrderBy(assignment => assignment.Role.ApplicationSystem!.Code)
            .ThenBy(assignment => assignment.Role.DisplayName)
            .Select(assignment => new DirectoryGroupRoleDto
            {
                Id = assignment.RoleId,
                Name = assignment.Role.DisplayName,
                ApplicationSystemId = assignment.Role.ApplicationSystemId!.Value,
                ApplicationCode = assignment.Role.ApplicationSystem!.Code
            })
            .ToList()
    };

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
