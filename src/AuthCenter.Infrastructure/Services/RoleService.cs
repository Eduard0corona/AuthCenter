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

    public RoleService(AuthCenterDbContext db, RoleManager<ApplicationRole> roleManager, IDateTimeProvider dateTimeProvider)
    {
        _db = db;
        _roleManager = roleManager;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<PagedResult<RoleDto>> GetAllAsync(PaginationQuery pagination, CancellationToken ct = default)
    {
        var query = _db.Roles
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Permission)
            .AsNoTracking()
            .OrderBy(r => r.DisplayName);

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
        return OperationResult<RoleDto>.Success(MapToDto(created));
    }

    public async Task<OperationResult<RoleDto>> UpdateAsync(Guid id, UpdateRoleRequest request, CancellationToken ct = default)
    {
        var role = await _roleManager.FindByIdAsync(id.ToString());
        if (role is null)
            return OperationResult<RoleDto>.Failure("ROLE_NOT_FOUND", "Role not found.");

        var applicationCode = role.ApplicationSystemId.HasValue
            ? await _db.ApplicationSystems.Where(app => app.Id == role.ApplicationSystemId).Select(app => app.Code).SingleAsync(ct)
            : "GLOBAL";
        role.DisplayName = request.Name;
        role.Name = $"{applicationCode}:{request.Name}";
        role.NormalizedName = role.Name.ToUpperInvariant();
        role.Description = request.Description;

        var result = await _roleManager.UpdateAsync(role);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description).ToList();
            return OperationResult<RoleDto>.Failure("ROLE_UPDATE_FAILED", string.Join(", ", errors));
        }

        return OperationResult<RoleDto>.Success(MapToDto(role));
    }

    public async Task<OperationResult> AddPermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default)
    {
        var role = await _db.Roles.FindAsync([roleId], ct);
        if (role is null)
            return OperationResult.Failure("ROLE_NOT_FOUND", "Role not found.");

        var permission = await _db.Permissions.FindAsync([permissionId], ct);
        if (permission is null)
            return OperationResult.Failure("PERMISSION_NOT_FOUND", "Permission not found.");

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
        return OperationResult.Success();
    }

    public async Task<OperationResult> RemovePermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default)
    {
        var rp = await _db.RolePermissions
            .FirstOrDefaultAsync(x => x.RoleId == roleId && x.PermissionId == permissionId, ct);
        if (rp is null)
            return OperationResult.Failure("PERMISSION_NOT_ASSIGNED", "Permission is not assigned to this role.");

        _db.RolePermissions.Remove(rp);
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ActivateAsync(Guid id, CancellationToken ct = default)
    {
        var role = await _db.Roles.FindAsync([id], ct);
        if (role is null)
            return OperationResult.Failure("ROLE_NOT_FOUND", "Role not found.");

        role.IsActive = true;
        await _db.SaveChangesAsync(ct);
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
        return OperationResult.Success();
    }

    public async Task<IList<string>> GetPermissionCodesForUserAsync(Guid userId, CancellationToken ct = default) =>
        await _db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Join(_db.RolePermissions, ur => ur.RoleId, rp => rp.RoleId, (_, rp) => rp)
            .Where(rp => rp.Role.IsActive && rp.Permission.IsActive)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToListAsync(ct);

    public async Task<IList<string>> GetPermissionCodesForUserAsync(Guid userId, Guid applicationSystemId, CancellationToken ct = default) =>
        await _db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Join(_db.RolePermissions, ur => ur.RoleId, rp => rp.RoleId, (_, rp) => rp)
            .Where(rp =>
                rp.Role.IsActive &&
                rp.Permission.IsActive &&
                rp.Permission.ApplicationSystemId == applicationSystemId &&
                (!rp.Role.ApplicationSystemId.HasValue || rp.Role.ApplicationSystemId == applicationSystemId))
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToListAsync(ct);

    public async Task<IList<string>> GetRoleNamesForUserAsync(Guid userId, CancellationToken ct = default) =>
        await _db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r)
            .Where(r => r.IsActive)
            .Select(r => r.DisplayName)
            .ToListAsync(ct);

    public async Task<IList<string>> GetRoleNamesForUserAsync(Guid userId, Guid applicationSystemId, CancellationToken ct = default) =>
        await _db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r)
            .Where(r =>
                r.IsActive &&
                (!r.ApplicationSystemId.HasValue || r.ApplicationSystemId == applicationSystemId))
            .Select(r => r.DisplayName)
            .ToListAsync(ct);

    private static RoleDto MapToDto(ApplicationRole role) => new()
    {
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
