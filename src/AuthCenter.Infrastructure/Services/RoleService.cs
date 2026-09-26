using AuthCenter.Application.Common;
using AuthCenter.Application.Common.Exceptions;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Requests.Roles;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Roles;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public class RoleService : IRoleService
{
    private readonly AuthCenterDbContext _db;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IAuditService _audit;

    public RoleService(AuthCenterDbContext db, RoleManager<ApplicationRole> roleManager, IDateTimeProvider dateTimeProvider, IAuditService audit)
    {
        _db = db;
        _roleManager = roleManager;
        _dateTimeProvider = dateTimeProvider;
        _audit = audit;
    }

    public async Task<PagedResult<RoleDto>> GetAllAsync(PaginationQuery pagination, Guid? applicationSystemId = null, CancellationToken ct = default)
    {
        var query = _db.Roles
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Permission)
            .AsNoTracking();

        if (applicationSystemId.HasValue)
            query = query.Where(role => role.ApplicationSystemId == applicationSystemId.Value);

        query = query.OrderBy(r => r.DisplayName);

        var totalCount = await query.CountAsync(ct);
        var roles = await query.Skip(pagination.Skip).Take(pagination.PageSize).ToListAsync(ct);

        return PagedResult<RoleDto>.Create(roles.Select(MapToDto).ToList(), totalCount, pagination.Page, pagination.PageSize);
    }

    public async Task<RoleDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var role = await _db.Roles
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Permission)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        return role is null ? null : MapToDto(role);
    }

    public async Task<OperationResult<RoleDto>> CreateAsync(CreateRoleRequest request, CancellationToken ct = default)
    {
        if (request.IsSystemRole)
            return OperationResult<RoleDto>.Failure("SYSTEM_ROLE_RESERVED", "System roles can only be provisioned by trusted server-side bootstrap operations.");

        if (await _db.Roles.AnyAsync(r => r.ApplicationSystemId == request.ApplicationSystemId && r.DisplayName == request.Name, ct))
            return OperationResult<RoleDto>.Failure("ROLE_EXISTS", $"Role '{request.Name}' already exists.");

        var applicationCode = request.ApplicationSystemId.HasValue
            ? await _db.ApplicationSystems.Where(app => app.Id == request.ApplicationSystemId).Select(app => app.Code).SingleOrDefaultAsync(ct)
            : "GLOBAL";
        if (applicationCode is null)
            return OperationResult<RoleDto>.Failure("APP_NOT_FOUND", "Application not found.");
        var storageName = $"{applicationCode}:{request.Name}";

        var role = new ApplicationRole
        {
            Id = Guid.NewGuid(),
            Name = storageName,
            NormalizedName = storageName.ToUpperInvariant(),
            DisplayName = request.Name,
            Description = request.Description,
            ApplicationSystemId = request.ApplicationSystemId,
            IsSystemRole = request.IsSystemRole,
            IsActive = true,
            CreatedAt = _dateTimeProvider.UtcNow
        };

        var result = await _roleManager.CreateAsync(role);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description).ToList();
            return OperationResult<RoleDto>.Failure("ROLE_CREATION_FAILED", string.Join(", ", errors));
        }

        // Re-query with RolePermissions included so MapToDto doesn't NRE on the navigation property
        var created = await _db.Roles
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .AsNoTracking()
            .FirstAsync(r => r.Id == role.Id);
        await AuditAsync("ROLE_CREATED", created, new { result = "Success" }, ct);
        return OperationResult<RoleDto>.Success(MapToDto(created));
    }

    public async Task<OperationResult<RoleDto>> UpdateAsync(Guid id, UpdateRoleRequest request, CancellationToken ct = default)
    {
        var role = await _roleManager.FindByIdAsync(id.ToString());
        if (role is null)
            return OperationResult<RoleDto>.Failure("ROLE_NOT_FOUND", "Role not found.");
        if (role.IsSystemRole)
            return OperationResult<RoleDto>.Failure("SYSTEM_ROLE_PROTECTED", "System roles cannot be modified through the administrative API.");

        var applicationCode = role.ApplicationSystemId.HasValue
            ? await _db.ApplicationSystems.Where(app => app.Id == role.ApplicationSystemId).Select(app => app.Code).SingleAsync(ct)
            : "GLOBAL";
        if (!role.TryAdvance(request.Version))
            return OperationResult<RoleDto>.Failure(VersionedUpdates.ConflictCode, "The role changed after it was loaded.");
        role.DisplayName = request.Name;
        role.Name = $"{applicationCode}:{request.Name}";
        role.NormalizedName = role.Name.ToUpperInvariant();
        role.Description = request.Description;

        var result = await _roleManager.UpdateAsync(role);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
                return OperationResult<RoleDto>.Failure(VersionedUpdates.ConflictCode, "The role changed after it was loaded.");
            var errors = result.Errors.Select(e => e.Description).ToList();
            return OperationResult<RoleDto>.Failure("ROLE_UPDATE_FAILED", string.Join(", ", errors));
        }

        var updated = await _db.Roles
            .Include(item => item.RolePermissions).ThenInclude(item => item.Permission)
            .AsNoTracking()
            .SingleAsync(item => item.Id == role.Id, ct);
        await AuditAsync("ROLE_UPDATED", updated, new { result = "Success" }, ct);
        return OperationResult<RoleDto>.Success(MapToDto(updated));
    }

    public async Task<OperationResult> AddPermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default)
    {
        var role = await _db.Roles.FindAsync([roleId], ct);
        if (role is null)
            return OperationResult.Failure("ROLE_NOT_FOUND", "Role not found.");
        if (role.IsSystemRole)
            return OperationResult.Failure("SYSTEM_ROLE_PROTECTED", "System role permissions cannot be changed through the administrative API.");

        var permission = await _db.Permissions.FindAsync([permissionId], ct);
        if (permission is null)
            return OperationResult.Failure("PERMISSION_NOT_FOUND", "Permission not found.");
        if (!permission.IsActive)
            return OperationResult.Failure("INACTIVE_PERMISSION", "Inactive permissions cannot be assigned to a role.");

        if (role.ApplicationSystemId.HasValue && role.ApplicationSystemId.Value != permission.ApplicationSystemId)
            return OperationResult.Failure("PERMISSION_APP_MISMATCH", "Permission must belong to the same application as the role.");

        var exists = await _db.RolePermissions
            .AnyAsync(rp => rp.RoleId == roleId && rp.PermissionId == permissionId, ct);

        if (exists)
            return OperationResult.Failure("PERMISSION_ALREADY_ASSIGNED", "Permission is already assigned to this role.");

        _db.RolePermissions.Add(new RolePermission
        {
            RoleId = roleId,
            PermissionId = permissionId,
            CreatedAt = _dateTimeProvider.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        await AuditAsync("ROLE_PERMISSION_GRANTED", role, new { result = "Success", permissionId }, ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RemovePermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default)
    {
        var role = await _db.Roles.SingleOrDefaultAsync(item => item.Id == roleId, ct);
        if (role?.IsSystemRole == true)
            return OperationResult.Failure("SYSTEM_ROLE_PROTECTED", "System role permissions cannot be changed through the administrative API.");

        var rp = await _db.RolePermissions
            .FirstOrDefaultAsync(x => x.RoleId == roleId && x.PermissionId == permissionId, ct);
        if (rp is null)
            return OperationResult.Failure("PERMISSION_NOT_ASSIGNED", "Permission is not assigned to this role.");

        _db.RolePermissions.Remove(rp);
        await _db.SaveChangesAsync(ct);
        if (role is not null) await AuditAsync("ROLE_PERMISSION_REVOKED", role, new { result = "Success", permissionId }, ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult<RoleDto>> SetPermissionsAsync(Guid roleId, SetRolePermissionsRequest request, CancellationToken ct = default)
    {
        var role = await _db.Roles
            .Include(item => item.RolePermissions)
            .FirstOrDefaultAsync(item => item.Id == roleId, ct);
        if (role is null)
            return OperationResult<RoleDto>.Failure("ROLE_NOT_FOUND", "Role not found.");
        if (role.IsSystemRole)
            return OperationResult<RoleDto>.Failure("SYSTEM_ROLE_PROTECTED", "System role permissions cannot be changed through the administrative API.");

        var permissionIds = request.PermissionIds.Distinct().ToArray();
        var permissions = await _db.Permissions
            .Where(item => permissionIds.Contains(item.Id))
            .ToListAsync(ct);
        if (permissions.Count != permissionIds.Length)
            return OperationResult<RoleDto>.Failure("PERMISSION_NOT_FOUND", "One or more permissions were not found.");
        if (permissions.Any(item => !item.IsActive))
            return OperationResult<RoleDto>.Failure("INACTIVE_PERMISSION", "Inactive permissions cannot be assigned to a role.");
        if (role.ApplicationSystemId.HasValue && permissions.Any(item => item.ApplicationSystemId != role.ApplicationSystemId.Value))
            return OperationResult<RoleDto>.Failure("PERMISSION_APP_MISMATCH", "Every permission must belong to the same application as the role.");

        var requestedIds = permissionIds.ToHashSet();
        var existingIds = role.RolePermissions.Select(item => item.PermissionId).ToHashSet();
        _db.RolePermissions.RemoveRange(role.RolePermissions.Where(item => !requestedIds.Contains(item.PermissionId)));
        _db.RolePermissions.AddRange(permissionIds.Where(permissionId => !existingIds.Contains(permissionId)).Select(permissionId => new RolePermission
        {
            RoleId = role.Id,
            PermissionId = permissionId,
            CreatedAt = _dateTimeProvider.UtcNow
        }));
        await _db.SaveChangesAsync(ct);

        var updated = await _db.Roles
            .Include(item => item.RolePermissions).ThenInclude(item => item.Permission)
            .AsNoTracking()
            .SingleAsync(item => item.Id == role.Id, ct);
        await AuditAsync("ROLE_PERMISSIONS_REPLACED", updated, new { result = "Success", permissionIds }, ct);
        return OperationResult<RoleDto>.Success(MapToDto(updated));
    }

    public async Task<OperationResult> ActivateAsync(Guid id, CancellationToken ct = default)
    {
        var role = await _db.Roles.FindAsync([id], ct);
        if (role is null)
            return OperationResult.Failure("ROLE_NOT_FOUND", "Role not found.");
        if (role.IsSystemRole)
            return OperationResult.Failure("SYSTEM_ROLE_PROTECTED", "System roles cannot be activated through the administrative API.");

        role.IsActive = true;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("ROLE_ACTIVATED", role, new { result = "Success" }, ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeactivateAsync(Guid id, CancellationToken ct = default)
    {
        var role = await _db.Roles.FindAsync([id], ct);
        if (role is null)
            return OperationResult.Failure("ROLE_NOT_FOUND", "Role not found.");

        if (role.IsSystemRole)
            return OperationResult.Failure("SYSTEM_ROLE", "System roles cannot be deactivated.");

        role.IsActive = false;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("ROLE_DEACTIVATED", role, new { result = "Success" }, ct);
        return OperationResult.Success();
    }

    public async Task<IList<string>> GetPermissionCodesForUserAsync(Guid userId, CancellationToken ct = default) =>
        await GetEffectiveRoleIds(userId, null)
            .Join(_db.RolePermissions, roleId => roleId, rp => rp.RoleId, (_, rp) => rp)
            .Where(rp => rp.Role.IsActive && rp.Permission.IsActive)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToListAsync(ct);

    private async Task AuditAsync(string action, ApplicationRole role, object metadata, CancellationToken ct)
    {
        var applicationCode = role.ApplicationSystemId.HasValue
            ? await _db.ApplicationSystems.Where(x => x.Id == role.ApplicationSystemId).Select(x => x.Code).SingleOrDefaultAsync(ct)
            : null;
        await _audit.LogAsync(action, applicationCode: applicationCode, entityName: nameof(ApplicationRole), entityId: role.Id.ToString(), metadata: metadata, ct: ct);
    }

    public async Task<IList<string>> GetPermissionCodesForUserAsync(Guid userId, Guid applicationSystemId, CancellationToken ct = default) =>
        await GetEffectiveRoleIds(userId, applicationSystemId)
            .Join(_db.RolePermissions, roleId => roleId, rp => rp.RoleId, (_, rp) => rp)
            .Where(rp =>
                rp.Role.IsActive &&
                rp.Permission.IsActive &&
                rp.Permission.ApplicationSystemId == applicationSystemId &&
                (!rp.Role.ApplicationSystemId.HasValue || rp.Role.ApplicationSystemId == applicationSystemId))
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToListAsync(ct);

    public async Task<IList<string>> GetRoleNamesForUserAsync(Guid userId, CancellationToken ct = default) =>
        await GetEffectiveRoleIds(userId, null)
            .Join(_db.Roles, roleId => roleId, role => role.Id, (_, role) => role)
            .Where(r => r.IsActive)
            .Select(r => r.DisplayName)
            .Distinct()
            .ToListAsync(ct);

    public async Task<IList<string>> GetRoleNamesForUserAsync(Guid userId, Guid applicationSystemId, CancellationToken ct = default) =>
        await GetEffectiveRoleIds(userId, applicationSystemId)
            .Join(_db.Roles, roleId => roleId, role => role.Id, (_, role) => role)
            .Where(r =>
                r.IsActive &&
                (!r.ApplicationSystemId.HasValue || r.ApplicationSystemId == applicationSystemId))
            .Select(r => r.DisplayName)
            .Distinct()
            .ToListAsync(ct);

    private IQueryable<Guid> GetEffectiveRoleIds(Guid userId, Guid? applicationSystemId)
    {
        var directRoleIds = _db.UserRoles
            .Where(userRole => userRole.UserId == userId)
            .Select(userRole => userRole.RoleId);

        var groupRoleIds = _db.UserGroupMemberships
            .Where(membership => membership.UserId == userId && membership.Group.IsActive)
            .SelectMany(membership => membership.Group.RoleAssignments)
            .Where(assignment =>
                assignment.Role.IsActive &&
                assignment.Role.ApplicationSystemId.HasValue &&
                assignment.Group.ApplicationAssignments.Any(application =>
                    application.ApplicationSystemId == assignment.Role.ApplicationSystemId.Value));

        if (applicationSystemId.HasValue)
            groupRoleIds = groupRoleIds.Where(assignment => assignment.Role.ApplicationSystemId == applicationSystemId.Value);

        return directRoleIds.Union(groupRoleIds.Select(assignment => assignment.RoleId));
    }

    private static RoleDto MapToDto(ApplicationRole role) => new()
    {
        Version = role.Version,
        Id = role.Id,
        Name = role.DisplayName,
        Description = role.Description,
        ApplicationSystemId = role.ApplicationSystemId,
        IsSystemRole = role.IsSystemRole,
        IsActive = role.IsActive,
        CreatedAt = role.CreatedAt,
        Permissions = role.RolePermissions.Select(rp => rp.Permission.Code).ToList()
    };
}
