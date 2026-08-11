using AuthCenter.Application.Common;
using AuthCenter.Application.Common.Exceptions;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Requests.Users;
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
    private readonly IEmailService _emailService;
    private readonly IActionLinkService _actionLinkService;
    private readonly IRefreshTokenService _refreshTokens;

    public UserAccessService(
        AuthCenterDbContext db,
        UserManager<ApplicationUser> userManager,
        IDateTimeProvider dateTimeProvider,
        IEmailService emailService,
        IActionLinkService actionLinkService,
        IRefreshTokenService refreshTokens)
    {
        _db = db;
        _userManager = userManager;
        _dateTimeProvider = dateTimeProvider;
        _emailService = emailService;
        _actionLinkService = actionLinkService;
        _refreshTokens = refreshTokens;
    }

    public async Task<PagedResult<UserDto>> GetAllUsersAsync(PaginationQuery pagination, CancellationToken ct = default)
        => await GetAllUsersAsync(new UserQuery { Page = pagination.Page, PageSize = pagination.PageSize }, ct);

    public async Task<PagedResult<UserDto>> GetAllUsersAsync(UserQuery pagination, CancellationToken ct = default)
    {
        var query = _db.Users
            .Where(u => u.DeletedAt == null)
            .Include(u => u.ApplicationAccesses)
                .ThenInclude(a => a.ApplicationSystem)
            .Include(u => u.GroupMemberships)
                .ThenInclude(membership => membership.Group)
                    .ThenInclude(group => group.ApplicationAssignments)
                        .ThenInclude(assignment => assignment.ApplicationSystem)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(pagination.Search))
        {
            var search = pagination.Search.Trim();
            var normalizedSearch = search.ToUpperInvariant();
            query = query.Where(u => u.FullName.StartsWith(search) ||
                (u.NormalizedEmail != null && u.NormalizedEmail.StartsWith(normalizedSearch)));
        }

        if (pagination.ApplicationSystemId.HasValue)
            query = query.Where(u =>
                u.ApplicationAccesses.Any(a => a.ApplicationSystemId == pagination.ApplicationSystemId.Value) ||
                u.GroupMemberships.Any(membership =>
                    membership.Group.IsActive &&
                    membership.Group.ApplicationAssignments.Any(assignment =>
                        assignment.ApplicationSystemId == pagination.ApplicationSystemId.Value)));

        if (pagination.IsActive.HasValue)
            query = query.Where(u => u.IsActive == pagination.IsActive.Value);

        if (pagination.HasPendingAccess.HasValue)
        {
            // HasPendingAccess=true  → user has at least one inactive (pending) access record
            // HasPendingAccess=false → user has no inactive access records at all
            query = pagination.HasPendingAccess.Value
                ? query.Where(u => u.ApplicationAccesses.Any(a => !a.IsActive))
                : query.Where(u => u.ApplicationAccesses.All(a => a.IsActive));
        }

        query = query.OrderBy(u => u.FullName);

        var totalCount = await query.CountAsync(ct);
        var users = await query.Skip(pagination.Skip).Take(pagination.PageSize).ToListAsync(ct);

        var userIds = users.Select(u => u.Id).ToList();
        var rolesByUser = await _db.UserRoles
            .Where(ur => userIds.Contains(ur.UserId))
            .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, RoleName = r.DisplayName })
            .AsNoTracking()
            .ToListAsync(ct);

        var groupRolesByUser = await _db.UserGroupMemberships
            .Where(membership => userIds.Contains(membership.UserId) && membership.Group.IsActive)
            .SelectMany(membership => membership.Group.RoleAssignments.Select(assignment => new
            {
                membership.UserId,
                RoleName = assignment.Role.DisplayName,
                assignment.Role.IsActive,
                assignment.Role.ApplicationSystemId,
                HasApplicationAccess = assignment.Role.ApplicationSystemId.HasValue &&
                    membership.Group.ApplicationAssignments.Any(application =>
                        application.ApplicationSystemId == assignment.Role.ApplicationSystemId.Value)
            }))
            .Where(item => item.IsActive && item.HasApplicationAccess)
            .AsNoTracking()
            .ToListAsync(ct);

        var roleMap = rolesByUser.Concat(groupRolesByUser.Select(item => new { item.UserId, item.RoleName }))
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => (IList<string>)g.Select(x => x.RoleName).Distinct().ToList());

        var dtos = users
            .Select(u => MapToDto(u, roleMap.TryGetValue(u.Id, out var r) ? r : []))
            .ToList();

        return PagedResult<UserDto>.Create(dtos, totalCount, pagination.Page, pagination.PageSize);
    }

    public async Task<OperationResult<UserDto>> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        if (await _userManager.FindByEmailAsync(request.Email) is not null)
            return OperationResult<UserDto>.Failure("EMAIL_TAKEN", "An account with this email already exists.");

        // Validate all pre-conditions before writing anything
        if (request.GrantApplicationAccess && !request.ApplicationSystemId.HasValue)
            return OperationResult<UserDto>.Failure("APP_REQUIRED", "ApplicationSystemId is required when granting access.");

        if (request.RoleIds.Count > 0)
        {
            foreach (var roleId in request.RoleIds.Distinct())
            {
                var role = await _db.Roles.FindAsync([roleId], ct);
                if (role is null)
                    return OperationResult<UserDto>.Failure("ROLE_NOT_FOUND", $"Role {roleId} not found.");

                if (request.ApplicationSystemId.HasValue &&
                    role.ApplicationSystemId.HasValue &&
                    role.ApplicationSystemId.Value != request.ApplicationSystemId.Value)
                    return OperationResult<UserDto>.Failure("ROLE_APP_MISMATCH", "Role must belong to the selected application.");
            }
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            var now = _dateTimeProvider.UtcNow;
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                FullName = request.FullName,
                Email = request.Email,
                UserName = request.Email,
                EmailConfirmed = true,
                HasLocalPassword = !string.IsNullOrWhiteSpace(request.Password),
                IsActive = true,
                CreatedAt = now
            };

            await using var transaction = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(ct)
                : null;

            var result = string.IsNullOrWhiteSpace(request.Password)
                ? await _userManager.CreateAsync(user)
                : await _userManager.CreateAsync(user, request.Password);

            if (!result.Succeeded)
                return OperationResult<UserDto>.Failure("USER_CREATION_FAILED", string.Join(", ", result.Errors.Select(error => error.Description)));

            if (request.GrantApplicationAccess)
                await GrantAccessAsync(user.Id, request.ApplicationSystemId!.Value, request.ApplicationAccessIsActive, ct);

            var roleResult = await AssignRolesAsync(user, request.RoleIds, request.ApplicationSystemId, ct);
            if (!roleResult.IsSuccess)
                return OperationResult<UserDto>.Failure(roleResult.ErrorCode, roleResult.Message);

            var response = OperationResult<UserDto>.Success((await GetUserByIdAsync(user.Id, ct))!);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return response;
        });
    }

    public async Task<OperationResult<UserDto>> InviteUserAsync(InviteUserRequest request, CancellationToken ct = default)
    {
        var app = await _db.ApplicationSystems
            .Include(a => a.RegistrationSettings)
            .FirstOrDefaultAsync(a => a.Id == request.ApplicationSystemId, ct);

        if (app is null || !app.IsActive)
            return OperationResult<UserDto>.Failure("APP_NOT_FOUND", "Application not found or inactive.");

        if (!IsEmailDomainAllowed(request.Email, app.RegistrationSettings?.AllowedEmailDomains))
            return OperationResult<UserDto>.Failure("EMAIL_DOMAIN_NOT_ALLOWED", "Email domain is not allowed for this application.");

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            var user = await _userManager.FindByEmailAsync(request.Email);
            var now = _dateTimeProvider.UtcNow;
            await using var transaction = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(ct)
                : null;

            if (user is null)
            {
                user = new ApplicationUser
                {
                    Id = Guid.NewGuid(),
                    FullName = request.FullName,
                    Email = request.Email,
                    UserName = request.Email,
                    EmailConfirmed = true,
                    HasLocalPassword = false,
                    IsActive = true,
                    CreatedAt = now
                };

                var createResult = await _userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                    return OperationResult<UserDto>.Failure("USER_CREATION_FAILED", string.Join(", ", createResult.Errors.Select(error => error.Description)));
            }

            await GrantAccessAsync(user.Id, request.ApplicationSystemId, request.GrantActiveAccess, ct);

            var roleResult = await AssignRolesAsync(user, request.RoleIds, request.ApplicationSystemId, ct);
            if (!roleResult.IsSuccess)
                return OperationResult<UserDto>.Failure(roleResult.ErrorCode, roleResult.Message);

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var actionUrl = _actionLinkService.GetActionUrl(ActionLinkPurpose.Invitation, app.Code);
            await _emailService.SendInvitationAsync(user.Email!, user.FullName, app.Name, token, actionUrl, ct);

            var response = OperationResult<UserDto>.Success((await GetUserByIdAsync(user.Id, ct))!);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return response;
        });
    }

    public async Task<OperationResult<UserDto>> UpdateUserAsync(Guid userId, UpdateUserRequest request, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult<UserDto>.Failure("USER_NOT_FOUND", "User not found.");

        user.FullName = request.FullName;
        user.PictureUrl = request.PictureUrl;
        user.UpdatedAt = _dateTimeProvider.UtcNow;

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return OperationResult<UserDto>.Failure("USER_UPDATE_FAILED", string.Join(", ", result.Errors.Select(e => e.Description)));

        return OperationResult<UserDto>.Success((await GetUserByIdAsync(user.Id, ct))!);
    }

    public async Task<UserDto?> GetUserByIdAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users
            .Include(u => u.ApplicationAccesses)
                .ThenInclude(a => a.ApplicationSystem)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null) return null;

        var roles = await _db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.DisplayName)
            .ToListAsync(ct);

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
            .FirstOrDefaultAsync(a => a.UserId == userId && a.ApplicationSystemId == applicationSystemId, ct);
        if (access is null)
            return OperationResult.Failure("ACCESS_NOT_FOUND", "User does not have access to this application.");

        access.IsActive = false;
        access.RevokedAt = _dateTimeProvider.UtcNow;
        await _db.SaveChangesAsync(ct);
        var applicationCode = await _db.ApplicationSystems
            .Where(application => application.Id == applicationSystemId)
            .Select(application => application.Code)
            .SingleAsync(ct);
        await _refreshTokens.RevokeAllForUserAsync(userId, applicationCode, ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> AssignRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");

        var role = await _db.Roles.FindAsync([roleId], ct);
        if (role is null)
            return OperationResult.Failure("ROLE_NOT_FOUND", "Role not found.");

        if (role.Name is null)
            return OperationResult.Failure("INVALID_ROLE", "Role name is null.");

        var result = await _userManager.AddToRoleAsync(user, role.Name);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description).ToList();
            return OperationResult.Failure("ROLE_ASSIGN_FAILED", string.Join(", ", errors));
        }

        await _refreshTokens.RevokeAllForUserAsync(userId, ct);

        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");

        var role = await _db.Roles.FindAsync([roleId], ct);
        if (role is null)
            return OperationResult.Failure("ROLE_NOT_FOUND", "Role not found.");

        if (role.Name is null)
            return OperationResult.Failure("INVALID_ROLE", "Role name is null.");

        var result = await _userManager.RemoveFromRoleAsync(user, role.Name);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description).ToList();
            return OperationResult.Failure("ROLE_REMOVE_FAILED", string.Join(", ", errors));
        }


        await _refreshTokens.RevokeAllForUserAsync(userId, ct);

        return OperationResult.Success();
    }

    public async Task<OperationResult> ActivateUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");
        user.IsActive = true;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);
        await _refreshTokens.RevokeAllForUserAsync(userId, ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeactivateUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");
        user.IsActive = false;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);
        await _refreshTokens.RevokeAllForUserAsync(userId, ct);
        return OperationResult.Success();
    }

    public Task<bool> HasActiveAccessAsync(Guid userId, Guid applicationSystemId, CancellationToken ct = default) =>
        _db.Users.AnyAsync(user =>
            user.Id == userId &&
            (user.ApplicationAccesses.Any(access =>
                 access.ApplicationSystemId == applicationSystemId && access.IsActive) ||
             user.GroupMemberships.Any(membership =>
                 membership.Group.IsActive &&
                 membership.Group.ApplicationAssignments.Any(assignment =>
                     assignment.ApplicationSystemId == applicationSystemId && assignment.ApplicationSystem.IsActive))), ct);

    public async Task<IList<string>> GetApplicationCodesForUserAsync(Guid userId, CancellationToken ct = default) =>
        await _db.UserApplicationAccesses
            .Where(access => access.UserId == userId && access.IsActive && access.ApplicationSystem.IsActive)
            .Select(access => access.ApplicationSystem.Code)
            .Union(_db.UserGroupMemberships
                .Where(membership => membership.UserId == userId && membership.Group.IsActive)
                .SelectMany(membership => membership.Group.ApplicationAssignments)
                .Where(assignment => assignment.ApplicationSystem.IsActive)
                .Select(assignment => assignment.ApplicationSystem.Code))
            .Distinct()
            .ToListAsync(ct);

    public async Task<OperationResult> ForcePasswordChangeAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");

        if (!user.HasLocalPassword)
            return OperationResult.Failure("NO_LOCAL_PASSWORD", "User does not have a local password.");

        user.MustChangePassword = true;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return OperationResult.Failure("UPDATE_FAILED", string.Join(", ", result.Errors.Select(e => e.Description)));

        return OperationResult.Success();
    }

    public async Task<OperationResult> AdminDeleteUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");

        if (user.DeletedAt is not null)
            return OperationResult.Failure("USER_ALREADY_DELETED", "User is already deleted.");

        var now = _dateTimeProvider.UtcNow;
        user.DeletedAt = now;
        user.IsActive = false;
        user.UpdatedAt = now;
        user.FullName = "Deleted User";
        user.PictureUrl = null;
        user.PhoneNumber = null;

        var anonymizedEmail = $"deleted_{userId:N}@deleted.invalid";
        user.Email = anonymizedEmail;
        user.NormalizedEmail = anonymizedEmail.ToUpperInvariant();
        user.UserName = anonymizedEmail;
        user.NormalizedUserName = anonymizedEmail.ToUpperInvariant();

        await _userManager.UpdateAsync(user);
        await _refreshTokens.RevokeAllForUserAsync(userId, ct);
        return OperationResult.Success();
    }

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
            .Union(user.GroupMemberships
                .Where(membership => membership.Group.IsActive)
                .SelectMany(membership => membership.Group.ApplicationAssignments)
                .Where(assignment => assignment.ApplicationSystem.IsActive)
                .Select(assignment => assignment.ApplicationSystem.Code))
            .Distinct()
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

    private async Task<OperationResult> AssignRolesAsync(ApplicationUser user, IReadOnlyList<Guid> roleIds, Guid? applicationSystemId, CancellationToken ct)
    {
        var distinctIds = roleIds.Distinct().ToList();
        if (distinctIds.Count == 0) return OperationResult.Success();

        var roles = await _db.Roles
            .Where(r => distinctIds.Contains(r.Id))
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var roleId in distinctIds)
        {
            var role = roles.FirstOrDefault(r => r.Id == roleId);
            if (role is null)
                return OperationResult.Failure("ROLE_NOT_FOUND", $"Role {roleId} not found.");

            if (applicationSystemId.HasValue &&
                role.ApplicationSystemId.HasValue &&
                role.ApplicationSystemId.Value != applicationSystemId.Value)
                return OperationResult.Failure("ROLE_APP_MISMATCH", "Role must belong to the selected application.");

            if (role.Name is null)
                return OperationResult.Failure("INVALID_ROLE", "Role name is null.");
        }

        foreach (var role in roles)
        {
            var result = await _userManager.AddToRoleAsync(user, role.Name!);
            if (!result.Succeeded)
                return OperationResult.Failure("ROLE_ASSIGN_FAILED", string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        return OperationResult.Success();
    }

    private static bool IsEmailDomainAllowed(string email, string? allowedEmailDomains)
    {
        if (string.IsNullOrWhiteSpace(allowedEmailDomains))
            return true;

        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
            return false;

        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        var allowed = allowedEmailDomains
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.TrimStart('@').ToLowerInvariant());

        return allowed.Contains(domain);
    }
}
