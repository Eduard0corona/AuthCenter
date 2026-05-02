using AuthCenter.Application.Common;
using AuthCenter.Application.Common.Exceptions;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Users;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public class UserAccessService : IUserAccessService
{
    private readonly AuthCenterDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDateTimeProvider _dateTimeProvider;

    public UserAccessService(AuthCenterDbContext db, UserManager<ApplicationUser> userManager, IDateTimeProvider dateTimeProvider)
    {
        _db = db;
        _userManager = userManager;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<PagedResult<UserDto>> GetAllUsersAsync(PaginationQuery pagination, CancellationToken ct = default)
    {
        var query = _db.Users
            .Include(u => u.ApplicationAccesses)
                .ThenInclude(a => a.ApplicationSystem)
            .AsNoTracking()
            .OrderBy(u => u.FullName);

        var totalCount = await query.CountAsync(ct);
        var users = await query.Skip(pagination.Skip).Take(pagination.PageSize).ToListAsync(ct);

        var dtos = new List<UserDto>(users.Count);
        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);
            dtos.Add(MapToDto(user, roles));
        }

        return PagedResult<UserDto>.Create(dtos, totalCount, pagination.Page, pagination.PageSize);
    }

    public async Task<UserDto?> GetUserByIdAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users
            .Include(u => u.ApplicationAccesses)
                .ThenInclude(a => a.ApplicationSystem)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null) return null;
        var roles = await _userManager.GetRolesAsync(user);
        return MapToDto(user, roles);
    }

    public async Task<OperationResult> GrantAccessAsync(Guid userId, Guid applicationSystemId, bool isActive = true, CancellationToken ct = default)
    {
        var existing = await _db.UserApplicationAccesses
            .FirstOrDefaultAsync(a => a.UserId == userId && a.ApplicationSystemId == applicationSystemId, ct);

        if (existing is not null)
        {
            existing.IsActive = isActive;
            existing.RevokedAt = isActive ? null : _dateTimeProvider.UtcNow;
        }
        else
        {
            _db.UserApplicationAccesses.Add(new UserApplicationAccess
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ApplicationSystemId = applicationSystemId,
                IsActive = isActive,
                CreatedAt = _dateTimeProvider.UtcNow
            });
        }

        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ApproveApplicationAccessAsync(Guid userId, Guid applicationSystemId, CancellationToken ct = default)
    {
        var access = await _db.UserApplicationAccesses
            .FirstOrDefaultAsync(a => a.UserId == userId && a.ApplicationSystemId == applicationSystemId, ct);

        if (access is null)
            return OperationResult.Failure("ACCESS_NOT_FOUND", "User does not have pending access for this application.");

        if (access.IsActive)
            return OperationResult.Failure("ACCESS_ALREADY_ACTIVE", "User access is already active for this application.");

        access.IsActive = true;
        access.RevokedAt = null;
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RevokeAccessAsync(Guid userId, Guid applicationSystemId, CancellationToken ct = default)
    {
        var access = await _db.UserApplicationAccesses
            .FirstOrDefaultAsync(a => a.UserId == userId && a.ApplicationSystemId == applicationSystemId, ct)
            ?? throw new NotFoundException("UserApplicationAccess", $"{userId}/{applicationSystemId}");

        access.IsActive = false;
        access.RevokedAt = _dateTimeProvider.UtcNow;
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> AssignRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString())
            ?? throw new NotFoundException(nameof(ApplicationUser), userId);

        var role = await _db.Roles.FindAsync([roleId], ct)
            ?? throw new NotFoundException(nameof(ApplicationRole), roleId);

        if (role.Name is null)
            return OperationResult.Failure("INVALID_ROLE", "Role name is null.");

        var result = await _userManager.AddToRoleAsync(user, role.Name);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description).ToList();
            return OperationResult.Failure("ROLE_ASSIGN_FAILED", string.Join(", ", errors));
        }

        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString())
            ?? throw new NotFoundException(nameof(ApplicationUser), userId);

        var role = await _db.Roles.FindAsync([roleId], ct)
            ?? throw new NotFoundException(nameof(ApplicationRole), roleId);

        if (role.Name is null)
            return OperationResult.Failure("INVALID_ROLE", "Role name is null.");

        var result = await _userManager.RemoveFromRoleAsync(user, role.Name);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description).ToList();
            return OperationResult.Failure("ROLE_REMOVE_FAILED", string.Join(", ", errors));
        }

        return OperationResult.Success();
    }

    public async Task<OperationResult> ActivateUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString())
            ?? throw new NotFoundException(nameof(ApplicationUser), userId);
        user.IsActive = true;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeactivateUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString())
            ?? throw new NotFoundException(nameof(ApplicationUser), userId);
        user.IsActive = false;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);
        return OperationResult.Success();
    }

    public Task<bool> HasActiveAccessAsync(Guid userId, Guid applicationSystemId, CancellationToken ct = default) =>
        _db.UserApplicationAccesses
            .AnyAsync(a => a.UserId == userId && a.ApplicationSystemId == applicationSystemId && a.IsActive, ct);

    public async Task<IList<string>> GetApplicationCodesForUserAsync(Guid userId, CancellationToken ct = default) =>
        await _db.UserApplicationAccesses
            .Where(a => a.UserId == userId && a.IsActive)
            .Select(a => a.ApplicationSystem.Code)
            .ToListAsync(ct);

    private static UserDto MapToDto(ApplicationUser user, IList<string> roles) => new()
    {
        Id = user.Id,
        FullName = user.FullName,
        Email = user.Email ?? string.Empty,
        PictureUrl = user.PictureUrl,
        IsActive = user.IsActive,
        IsExternalUser = user.IsExternalUser,
        CreatedAt = user.CreatedAt,
        LastLoginAt = user.LastLoginAt,
        Roles = roles.ToList(),
        Applications = user.ApplicationAccesses
            .Where(a => a.IsActive)
            .Select(a => a.ApplicationSystem.Code)
            .ToList(),
        ApplicationAccesses = user.ApplicationAccesses
            .OrderBy(a => a.ApplicationSystem.Code)
            .Select(a => new UserApplicationAccessDto
            {
                ApplicationId = a.ApplicationSystemId,
                ApplicationCode = a.ApplicationSystem.Code,
                ApplicationName = a.ApplicationSystem.Name,
                IsActive = a.IsActive,
                CreatedAt = a.CreatedAt,
                RevokedAt = a.RevokedAt
            })
            .ToList()
    };
}
