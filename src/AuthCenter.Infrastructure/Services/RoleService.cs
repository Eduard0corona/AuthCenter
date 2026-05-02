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
            .OrderBy(r => r.Name);

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
        if (await _roleManager.RoleExistsAsync(request.Name))
            return OperationResult<RoleDto>.Failure("ROLE_EXISTS", $"Role '{request.Name}' already exists.");

        var role = new ApplicationRole
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            NormalizedName = request.Name.ToUpperInvariant(),
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

        role.Name = request.Name;
        role.NormalizedName = request.Name.ToUpperInvariant();
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

    public async Task<IList<string>> GetPermissionCodesForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var roleIds = await _db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync(ct);

        return await _db.RolePermissions
            .Where(rp => roleIds.Contains(rp.RoleId) && rp.Role.IsActive && rp.Permission.IsActive)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IList<string>> GetPermissionCodesForUserAsync(Guid userId, Guid applicationSystemId, CancellationToken ct = default)
    {
        var roleIds = await _db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync(ct);

        return await _db.RolePermissions
            .Where(rp =>
                roleIds.Contains(rp.RoleId) &&
                rp.Role.IsActive &&
                rp.Permission.IsActive &&
                rp.Permission.ApplicationSystemId == applicationSystemId &&
                (!rp.Role.ApplicationSystemId.HasValue || rp.Role.ApplicationSystemId == applicationSystemId))
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IList<string>> GetRoleNamesForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var roleIds = await _db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync(ct);

        return await _db.Roles
            .Where(r => roleIds.Contains(r.Id) && r.IsActive && r.Name != null)
            .Select(r => r.Name!)
            .ToListAsync(ct);
    }

    public async Task<IList<string>> GetRoleNamesForUserAsync(Guid userId, Guid applicationSystemId, CancellationToken ct = default)
    {
        var roleIds = await _db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync(ct);

        return await _db.Roles
            .Where(r =>
                roleIds.Contains(r.Id) &&
                r.IsActive &&
                r.Name != null &&
                (!r.ApplicationSystemId.HasValue || r.ApplicationSystemId == applicationSystemId))
            .Select(r => r.Name!)
            .ToListAsync(ct);
    }

    private static RoleDto MapToDto(ApplicationRole role) => new()
    {
        Id = role.Id,
        Name = role.Name ?? string.Empty,
        Description = role.Description,
        ApplicationSystemId = role.ApplicationSystemId,
        IsSystemRole = role.IsSystemRole,
        IsActive = role.IsActive,
        CreatedAt = role.CreatedAt,
        Permissions = role.RolePermissions.Select(rp => rp.Permission.Code).ToList()
    };
}
