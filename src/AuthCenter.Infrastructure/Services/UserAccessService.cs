using System.Data;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Common.Exceptions;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Requests.Users;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Users;
using AuthCenter.Domain.Constants;
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
    private readonly ICurrentUserService _currentUser;

    public UserAccessService(
        AuthCenterDbContext db,
        UserManager<ApplicationUser> userManager,
        IDateTimeProvider dateTimeProvider,
        IEmailService emailService,
        IActionLinkService actionLinkService,
        IRefreshTokenService refreshTokens,
        ICurrentUserService currentUser)
    {
        _db = db;
        _userManager = userManager;
        _dateTimeProvider = dateTimeProvider;
        _emailService = emailService;
        _actionLinkService = actionLinkService;
        _refreshTokens = refreshTokens;
        _currentUser = currentUser;
    }

    public async Task<PagedResult<UserDto>> GetAllUsersAsync(PaginationQuery pagination, CancellationToken ct = default)
        => await GetAllUsersAsync(new UserQuery { Page = pagination.Page, PageSize = pagination.PageSize }, ct);

    public async Task<PagedResult<UserDto>> GetAllUsersAsync(UserQuery pagination, CancellationToken ct = default)
    {
        var query = _db.Users
            .Where(u => u.DeletedAt == null)
            .Include(u => u.MfaCredential)
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

        var descending = string.Equals(pagination.SortDirection, "desc", StringComparison.OrdinalIgnoreCase);
        query = pagination.SortBy.Trim().ToLowerInvariant() switch
        {
            "email" when descending => query.OrderByDescending(user => user.Email).ThenBy(user => user.Id),
            "email" => query.OrderBy(user => user.Email).ThenBy(user => user.Id),
            "createdat" when descending => query.OrderByDescending(user => user.CreatedAt).ThenBy(user => user.Id),
            "createdat" => query.OrderBy(user => user.CreatedAt).ThenBy(user => user.Id),
            "lastloginat" when descending => query.OrderByDescending(user => user.LastLoginAt).ThenBy(user => user.Id),
            "lastloginat" => query.OrderBy(user => user.LastLoginAt).ThenBy(user => user.Id),
            _ when descending => query.OrderByDescending(user => user.FullName).ThenBy(user => user.Id),
            _ => query.OrderBy(user => user.FullName).ThenBy(user => user.Id)
        };

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
        if (request.IsTemporaryPassword && string.IsNullOrWhiteSpace(request.Password))
            return OperationResult<UserDto>.Failure("TEMPORARY_PASSWORD_REQUIRED", "A temporary password is required.");
        if (request.IsTemporaryPassword && request.Password!.Length < 12)
            return OperationResult<UserDto>.Failure("TEMPORARY_PASSWORD_WEAK", "A temporary password must be at least 12 characters.");

        // Validate all pre-conditions before writing anything
        if (request.GrantApplicationAccess && !request.ApplicationSystemId.HasValue)
            return OperationResult<UserDto>.Failure("APP_REQUIRED", "ApplicationSystemId is required when granting access.");

        if (request.RoleIds.Count > 0)
        {
            if (!request.GrantApplicationAccess || !request.ApplicationSystemId.HasValue)
                return OperationResult<UserDto>.Failure("ROLE_APP_ACCESS_REQUIRED", "Direct roles require direct application access.");
            foreach (var roleId in request.RoleIds.Distinct())
            {
                var role = await _db.Roles.FindAsync([roleId], ct);
                if (role is null)
                    return OperationResult<UserDto>.Failure("ROLE_NOT_FOUND", $"Role {roleId} not found.");

                if (request.ApplicationSystemId.HasValue &&
                    role.ApplicationSystemId.HasValue &&
                    role.ApplicationSystemId.Value != request.ApplicationSystemId.Value)
                    return OperationResult<UserDto>.Failure("ROLE_APP_MISMATCH", "Role must belong to the selected application.");
                if (!role.IsActive || !role.ApplicationSystemId.HasValue)
                    return OperationResult<UserDto>.Failure("ROLE_INVALID", "Only active application roles can be assigned directly.");
                if (role.IsSystemRole && !await CurrentUserIsEffectiveSuperAdminAsync(ct))
                    return OperationResult<UserDto>.Failure("SYSTEM_ROLE_ASSIGNMENT_FORBIDDEN", "Only an effective SuperAdmin can assign a system role.");
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
                MustChangePassword = request.IsTemporaryPassword && !string.IsNullOrWhiteSpace(request.Password),
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
            {
                var accessResult = await GrantAccessAsync(user.Id, request.ApplicationSystemId!.Value, request.ApplicationAccessIsActive, ct);
                if (!accessResult.IsSuccess)
                    return OperationResult<UserDto>.Failure(accessResult.ErrorCode, accessResult.Message);
            }

            var roleResult = await AssignRolesAsync(user, request.RoleIds, request.ApplicationSystemId, ct);
            if (!roleResult.IsSuccess)
                return OperationResult<UserDto>.Failure(roleResult.ErrorCode, roleResult.Message);

            AddAudit("USER_CREATED", user.Id, new { request.ApplicationSystemId, roleCount = request.RoleIds.Count, request.IsTemporaryPassword });
            await _db.SaveChangesAsync(ct);
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
        var requestedRoles = await _db.Roles.Where(role => request.RoleIds.Contains(role.Id)).ToListAsync(ct);
        if (requestedRoles.Count != request.RoleIds.Distinct().Count())
            return OperationResult<UserDto>.Failure("ROLE_NOT_FOUND", "One or more roles were not found.");
        if (requestedRoles.Any(role => !role.IsActive || role.ApplicationSystemId != request.ApplicationSystemId))
            return OperationResult<UserDto>.Failure("ROLE_APP_MISMATCH", "Only active roles from the invitation application can be assigned.");
        if (requestedRoles.Any(role => role.IsSystemRole) && !await CurrentUserIsEffectiveSuperAdminAsync(ct))
            return OperationResult<UserDto>.Failure("SYSTEM_ROLE_ASSIGNMENT_FORBIDDEN", "Only an effective SuperAdmin can assign a system role.");

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

            var accessResult = await GrantAccessAsync(user.Id, request.ApplicationSystemId, request.GrantActiveAccess, ct);
            if (!accessResult.IsSuccess)
                return OperationResult<UserDto>.Failure(accessResult.ErrorCode, accessResult.Message);

            var roleResult = await AssignRolesAsync(user, request.RoleIds, request.ApplicationSystemId, ct);
            if (!roleResult.IsSuccess)
                return OperationResult<UserDto>.Failure(roleResult.ErrorCode, roleResult.Message);

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var actionUrl = _actionLinkService.GetActionUrl(ActionLinkPurpose.Invitation, app.Code);
            await _emailService.SendInvitationAsync(user.Email!, user.FullName, app.Name, token, actionUrl, ct);

            AddAudit("USER_INVITED", user.Id, new { request.ApplicationSystemId, roleCount = request.RoleIds.Count, request.GrantActiveAccess });
            await _db.SaveChangesAsync(ct);
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

        AddAudit("USER_PROFILE_CORE_UPDATED", userId, new { request.FullName, hasPicture = request.PictureUrl is not null });
        await _db.SaveChangesAsync(ct);
        return OperationResult<UserDto>.Success((await GetUserByIdAsync(user.Id, ct))!);
    }

    public async Task<UserDto?> GetUserByIdAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users
            .Include(u => u.MfaCredential)
            .Include(u => u.ApplicationAccesses)
                .ThenInclude(a => a.ApplicationSystem)
            .Include(u => u.GroupMemberships)
                .ThenInclude(membership => membership.Group)
                    .ThenInclude(group => group.ApplicationAssignments)
                        .ThenInclude(assignment => assignment.ApplicationSystem)
            .Include(u => u.GroupMemberships)
                .ThenInclude(membership => membership.Group)
                    .ThenInclude(group => group.RoleAssignments)
                        .ThenInclude(assignment => assignment.Role)
                            .ThenInclude(role => role.ApplicationSystem)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null) return null;

        var directRoles = await _db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Join(_db.Roles.Include(role => role.ApplicationSystem), ur => ur.RoleId, role => role.Id, (_, role) => role)
            .AsNoTracking()
            .ToListAsync(ct);

        return MapDetailedDto(user, directRoles);
    }

    public async Task<OperationResult<UserDto>> SetDirectAccessAsync(
        Guid userId,
        SetUserDirectAccessRequest request,
        CancellationToken ct = default)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        var user = await _db.Users.FirstOrDefaultAsync(candidate => candidate.Id == userId, ct);
        if (user is null)
            return OperationResult<UserDto>.Failure("USER_NOT_FOUND", "User not found.");

        var applicationIds = request.ApplicationSystemIds.Distinct().ToHashSet();
        var roleIds = request.RoleIds.Distinct().ToHashSet();
        var applications = await _db.ApplicationSystems.Where(application => applicationIds.Contains(application.Id)).ToListAsync(ct);
        if (applications.Count != applicationIds.Count)
            return OperationResult<UserDto>.Failure("APP_NOT_FOUND", "One or more applications were not found.");
        if (applications.Any(application => !application.IsActive))
            return OperationResult<UserDto>.Failure("APP_INACTIVE", "Inactive applications cannot be assigned directly.");

        var roles = await _db.Roles.Where(role => roleIds.Contains(role.Id)).ToListAsync(ct);
        if (roles.Count != roleIds.Count)
            return OperationResult<UserDto>.Failure("ROLE_NOT_FOUND", "One or more roles were not found.");
        if (roles.Any(role => !role.IsActive || !role.ApplicationSystemId.HasValue))
            return OperationResult<UserDto>.Failure("ROLE_INVALID", "Only active application roles can be assigned directly.");
        if (roles.Any(role => !applicationIds.Contains(role.ApplicationSystemId!.Value)))
            return OperationResult<UserDto>.Failure("ROLE_APP_ACCESS_REQUIRED", "Every direct role requires direct application access in the same operation.");
        var existingRoleIds = await _db.UserRoles.Where(userRole => userRole.UserId == userId).Select(userRole => userRole.RoleId).ToListAsync(ct);
        var existingSystemRoleIds = await _db.Roles.Where(role => role.IsSystemRole && existingRoleIds.Contains(role.Id)).Select(role => role.Id).ToListAsync(ct);
        var requestedSystemRoleIds = roles.Where(role => role.IsSystemRole).Select(role => role.Id).ToHashSet();
        if (!requestedSystemRoleIds.SetEquals(existingSystemRoleIds) && !await CurrentUserIsEffectiveSuperAdminAsync(ct))
            return OperationResult<UserDto>.Failure("SYSTEM_ROLE_ASSIGNMENT_FORBIDDEN", "Only an effective SuperAdmin can change direct system-role assignments.");

        var superAdminRoleId = await GetSuperAdminRoleIdAsync(ct);
        var authCenterApplicationId = await GetAuthCenterApplicationIdAsync(ct);
        var keepsSuperAdmin = superAdminRoleId.HasValue && roleIds.Contains(superAdminRoleId.Value);
        var keepsAuthCenter = authCenterApplicationId.HasValue && applicationIds.Contains(authCenterApplicationId.Value);
        if (await IsEffectiveSuperAdminAsync(userId, ct) && (!keepsSuperAdmin || !keepsAuthCenter) &&
            !await HasAnotherEffectiveSuperAdminAsync(userId, ct))
            return OperationResult<UserDto>.Failure("LAST_SUPER_ADMIN", "The last effective SuperAdmin cannot lose AuthCenter access or its privileged role.");

        var existingAccess = await _db.UserApplicationAccesses.Where(access => access.UserId == userId).ToListAsync(ct);
        var now = _dateTimeProvider.UtcNow;
        foreach (var access in existingAccess)
        {
            if (applicationIds.Contains(access.ApplicationSystemId))
            {
                access.IsActive = true;
                access.RevokedAt = null;
            }
            else if (access.IsActive)
            {
                access.IsActive = false;
                access.RevokedAt = now;
            }
        }
        _db.UserApplicationAccesses.AddRange(applicationIds
            .Where(id => existingAccess.All(access => access.ApplicationSystemId != id))
            .Select(id => new UserApplicationAccess { Id = Guid.NewGuid(), UserId = userId, ApplicationSystemId = id, IsActive = true, CreatedAt = now }));
        _db.UserRoles.RemoveRange(_db.UserRoles.Where(userRole => userRole.UserId == userId && !roleIds.Contains(userRole.RoleId)));
        _db.UserRoles.AddRange(roleIds.Where(id => !existingRoleIds.Contains(id)).Select(id => new IdentityUserRole<Guid> { UserId = userId, RoleId = id }));
        AddAudit("USER_DIRECT_ACCESS_REPLACED", userId, new { applicationCount = applicationIds.Count, roleCount = roleIds.Count });
        await _db.SaveChangesAsync(ct);
        await _refreshTokens.RevokeAllForUserAsync(userId, ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return OperationResult<UserDto>.Success((await GetUserByIdAsync(userId, ct))!);
    }

    public async Task<OperationResult> GrantAccessAsync(Guid userId, Guid applicationSystemId, bool isActive = true, CancellationToken ct = default)
    {
        if (!await _db.Users.AnyAsync(user => user.Id == userId, ct))
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");
        var application = await _db.ApplicationSystems.FirstOrDefaultAsync(candidate => candidate.Id == applicationSystemId, ct);
        if (application is null)
            return OperationResult.Failure("APP_NOT_FOUND", "Application not found.");
        if (isActive && !application.IsActive)
            return OperationResult.Failure("APP_INACTIVE", "Inactive applications cannot be assigned.");
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

        AddAudit(isActive ? "USER_APPLICATION_ACCESS_GRANTED" : "USER_APPLICATION_ACCESS_PENDING", userId, new { applicationSystemId });
        await _db.SaveChangesAsync(ct);
        await _refreshTokens.RevokeAllForUserAsync(userId, application.Code, ct);
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
        AddAudit("USER_APPLICATION_ACCESS_APPROVED", userId, new { applicationSystemId });
        await _db.SaveChangesAsync(ct);
        var applicationCode = await _db.ApplicationSystems
            .Where(application => application.Id == applicationSystemId)
            .Select(application => application.Code)
            .SingleAsync(ct);
        await _refreshTokens.RevokeAllForUserAsync(userId, applicationCode, ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RevokeAccessAsync(Guid userId, Guid applicationSystemId, CancellationToken ct = default)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        var access = await _db.UserApplicationAccesses
            .FirstOrDefaultAsync(a => a.UserId == userId && a.ApplicationSystemId == applicationSystemId, ct);
        if (access is null)
            return OperationResult.Failure("ACCESS_NOT_FOUND", "User does not have access to this application.");
        if (await WouldRemoveLastSuperAdminAsync(userId, applicationSystemId, null, deactivateUser: false, ct))
            return OperationResult.Failure("LAST_SUPER_ADMIN", "The last effective SuperAdmin cannot lose AuthCenter access.");

        access.IsActive = false;
        access.RevokedAt = _dateTimeProvider.UtcNow;
        AddAudit("USER_APPLICATION_ACCESS_REVOKED", userId, new { applicationSystemId });
        await _db.SaveChangesAsync(ct);
        var applicationCode = await _db.ApplicationSystems
            .Where(application => application.Id == applicationSystemId)
            .Select(application => application.Code)
            .SingleAsync(ct);
        await _refreshTokens.RevokeAllForUserAsync(userId, applicationCode, ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
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
        if (!role.IsActive || !role.ApplicationSystemId.HasValue)
            return OperationResult.Failure("ROLE_INVALID", "Only active application roles can be assigned directly.");
        if (role.IsSystemRole && !await CurrentUserIsEffectiveSuperAdminAsync(ct))
            return OperationResult.Failure("SYSTEM_ROLE_ASSIGNMENT_FORBIDDEN", "Only an effective SuperAdmin can assign a system role.");
        if (!await _db.UserApplicationAccesses.AnyAsync(access => access.UserId == userId &&
            access.ApplicationSystemId == role.ApplicationSystemId.Value && access.IsActive && access.ApplicationSystem.IsActive, ct))
            return OperationResult.Failure("ROLE_APP_ACCESS_REQUIRED", "Grant direct application access before assigning this direct role.");

        var result = await _userManager.AddToRoleAsync(user, role.Name);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description).ToList();
            return OperationResult.Failure("ROLE_ASSIGN_FAILED", string.Join(", ", errors));
        }

        AddAudit("USER_ROLE_ASSIGNED", userId, new { roleId });
        await _db.SaveChangesAsync(ct);
        await _refreshTokens.RevokeAllForUserAsync(userId, ct);

        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");

        var role = await _db.Roles.FindAsync([roleId], ct);
        if (role is null)
            return OperationResult.Failure("ROLE_NOT_FOUND", "Role not found.");

        if (role.Name is null)
            return OperationResult.Failure("INVALID_ROLE", "Role name is null.");
        if (role.IsSystemRole && !await CurrentUserIsEffectiveSuperAdminAsync(ct))
            return OperationResult.Failure("SYSTEM_ROLE_ASSIGNMENT_FORBIDDEN", "Only an effective SuperAdmin can remove a system role.");
        if (await WouldRemoveLastSuperAdminAsync(userId, null, roleId, deactivateUser: false, ct))
            return OperationResult.Failure("LAST_SUPER_ADMIN", "The last effective SuperAdmin role cannot be removed.");

        var result = await _userManager.RemoveFromRoleAsync(user, role.Name);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description).ToList();
            return OperationResult.Failure("ROLE_REMOVE_FAILED", string.Join(", ", errors));
        }

        AddAudit("USER_ROLE_REMOVED", userId, new { roleId });
        await _db.SaveChangesAsync(ct);
        await _refreshTokens.RevokeAllForUserAsync(userId, ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

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
        AddAudit("USER_ACTIVATED", userId);
        await _db.SaveChangesAsync(ct);
        await _refreshTokens.RevokeAllForUserAsync(userId, ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeactivateUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");
        if (await WouldRemoveLastSuperAdminAsync(userId, null, null, deactivateUser: true, ct))
            return OperationResult.Failure("LAST_SUPER_ADMIN", "The last effective SuperAdmin cannot be deactivated.");
        user.IsActive = false;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);
        AddAudit("USER_DEACTIVATED", userId);
        await _db.SaveChangesAsync(ct);
        await _refreshTokens.RevokeAllForUserAsync(userId, ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
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

        AddAudit("USER_PASSWORD_CHANGE_FORCED", userId);
        await _db.SaveChangesAsync(ct);
        await _refreshTokens.RevokeAllForUserAsync(userId, ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> AdminDeleteUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");

        if (user.DeletedAt is not null)
            return OperationResult.Failure("USER_ALREADY_DELETED", "User is already deleted.");
        if (await WouldRemoveLastSuperAdminAsync(userId, null, null, deactivateUser: true, ct))
            return OperationResult.Failure("LAST_SUPER_ADMIN", "The last effective SuperAdmin cannot be deleted.");

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
        AddAudit("USER_ANONYMIZED", userId);
        await _db.SaveChangesAsync(ct);
        await _refreshTokens.RevokeAllForUserAsync(userId, ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
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
        HasLocalPassword = user.HasLocalPassword,
        MustChangePassword = user.MustChangePassword,
        MfaEnabled = user.MfaCredential?.IsEnabled == true,
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

    private static UserDto MapDetailedDto(ApplicationUser user, IReadOnlyList<ApplicationRole> directRoles)
    {
        var memberships = user.GroupMemberships.OrderBy(membership => membership.Group.Name).ToList();
        var applicationIds = user.ApplicationAccesses.Select(access => access.ApplicationSystemId)
            .Union(memberships.SelectMany(membership => membership.Group.ApplicationAssignments).Select(assignment => assignment.ApplicationSystemId))
            .Distinct()
            .ToList();
        var applications = applicationIds.Select(applicationId =>
        {
            var direct = user.ApplicationAccesses.FirstOrDefault(access => access.ApplicationSystemId == applicationId);
            var inherited = memberships
                .Where(membership => membership.Group.ApplicationAssignments.Any(assignment => assignment.ApplicationSystemId == applicationId))
                .Select(membership => new UserInheritedAccessSourceDto
                {
                    GroupId = membership.GroupId,
                    GroupName = membership.Group.Name,
                    IsActive = membership.Group.IsActive
                })
                .ToList();
            var application = direct?.ApplicationSystem ?? memberships
                .SelectMany(membership => membership.Group.ApplicationAssignments)
                .First(assignment => assignment.ApplicationSystemId == applicationId).ApplicationSystem;
            return new UserApplicationAssignmentDto
            {
                ApplicationId = application.Id,
                ApplicationCode = application.Code,
                ApplicationName = application.Name,
                IsApplicationActive = application.IsActive,
                IsDirect = direct is not null,
                DirectAccessStatus = direct is null ? null : direct.IsActive ? "Active" : direct.RevokedAt.HasValue ? "Revoked" : "Pending",
                IsEffective = application.IsActive && (direct?.IsActive == true || inherited.Any(source => source.IsActive)),
                InheritedFromGroups = inherited
            };
        }).OrderBy(assignment => assignment.ApplicationCode).ToList();
        var effectiveApplicationIds = applications.Where(assignment => assignment.IsEffective).Select(assignment => assignment.ApplicationId).ToHashSet();

        var inheritedRoleAssignments = memberships.SelectMany(membership => membership.Group.RoleAssignments.Select(assignment => new
        {
            Membership = membership,
            Role = assignment.Role,
            HasGroupApplication = assignment.Role.ApplicationSystemId.HasValue && membership.Group.ApplicationAssignments.Any(application =>
                application.ApplicationSystemId == assignment.Role.ApplicationSystemId.Value && application.ApplicationSystem.IsActive)
        })).ToList();
        var roleIds = directRoles.Select(role => role.Id).Union(inheritedRoleAssignments.Select(assignment => assignment.Role.Id)).Distinct().ToList();
        var roleAssignments = roleIds.Select(roleId =>
        {
            var direct = directRoles.FirstOrDefault(role => role.Id == roleId);
            var inheritedRoles = inheritedRoleAssignments.Where(assignment => assignment.Role.Id == roleId).ToList();
            var role = direct ?? inheritedRoles[0].Role;
            var inherited = inheritedRoles.Select(assignment => new UserInheritedAccessSourceDto
            {
                GroupId = assignment.Membership.GroupId,
                GroupName = assignment.Membership.Group.Name,
                IsActive = assignment.Membership.Group.IsActive && assignment.HasGroupApplication && role.IsActive
            }).ToList();
            var hasEffectiveApplication = !role.ApplicationSystemId.HasValue || effectiveApplicationIds.Contains(role.ApplicationSystemId.Value);
            return new UserRoleAssignmentDto
            {
                RoleId = role.Id,
                RoleName = role.DisplayName,
                ApplicationId = role.ApplicationSystemId,
                ApplicationCode = role.ApplicationSystem?.Code,
                IsRoleActive = role.IsActive,
                IsSystemRole = role.IsSystemRole,
                IsDirect = direct is not null,
                IsEffective = role.IsActive && hasEffectiveApplication && (direct is not null || inherited.Any(source => source.IsActive)),
                InheritedFromGroups = inherited
            };
        }).OrderBy(assignment => assignment.ApplicationCode).ThenBy(assignment => assignment.RoleName).ToList();

        return new UserDto
        {
            Id = user.Id,
            FullName = user.FullName,
            Email = user.Email ?? string.Empty,
            PictureUrl = user.PictureUrl,
            IsActive = user.IsActive,
            IsExternalUser = user.IsExternalUser,
            HasLocalPassword = user.HasLocalPassword,
            MustChangePassword = user.MustChangePassword,
            MfaEnabled = user.MfaCredential?.IsEnabled == true,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            Roles = roleAssignments.Where(assignment => assignment.IsEffective).Select(assignment => assignment.RoleName).Distinct().ToList(),
            Applications = applications.Where(assignment => assignment.IsEffective).Select(assignment => assignment.ApplicationCode).ToList(),
            ApplicationAccesses = user.ApplicationAccesses.OrderBy(access => access.ApplicationSystem.Code).Select(access => new UserApplicationAccessDto
            {
                ApplicationId = access.ApplicationSystemId,
                ApplicationCode = access.ApplicationSystem.Code,
                ApplicationName = access.ApplicationSystem.Name,
                IsActive = access.IsActive,
                CreatedAt = access.CreatedAt,
                RevokedAt = access.RevokedAt
            }).ToList(),
            ApplicationAssignments = applications,
            RoleAssignments = roleAssignments,
            GroupMemberships = memberships.Select(membership => new UserGroupMembershipDto
            {
                GroupId = membership.GroupId,
                GroupName = membership.Group.Name,
                IsGroupActive = membership.Group.IsActive,
                AddedAt = membership.CreatedAt
            }).ToList()
        };
    }

    private Task<Guid?> GetSuperAdminRoleIdAsync(CancellationToken ct) => _db.Roles
        .Where(role => role.IsSystemRole && role.DisplayName == DomainConstants.Roles.SuperAdmin)
        .Select(role => (Guid?)role.Id)
        .SingleOrDefaultAsync(ct);

    private Task<Guid?> GetAuthCenterApplicationIdAsync(CancellationToken ct) => _db.ApplicationSystems
        .Where(application => application.Code == DomainConstants.SystemCodes.AuthCenter)
        .Select(application => (Guid?)application.Id)
        .SingleOrDefaultAsync(ct);

    private Task<bool> HasInheritedApplicationAccessAsync(Guid userId, Guid applicationId, CancellationToken ct) =>
        _db.UserGroupMemberships.AnyAsync(membership => membership.UserId == userId && membership.Group.IsActive &&
            membership.Group.ApplicationAssignments.Any(assignment => assignment.ApplicationSystemId == applicationId && assignment.ApplicationSystem.IsActive), ct);

    private async Task<bool> IsEffectiveSuperAdminAsync(Guid userId, CancellationToken ct)
    {
        var roleId = await GetSuperAdminRoleIdAsync(ct);
        var applicationId = await GetAuthCenterApplicationIdAsync(ct);
        return roleId.HasValue && applicationId.HasValue && await IsEffectiveSuperAdminQuery(roleId.Value, applicationId.Value)
            .AnyAsync(user => user.Id == userId, ct);
    }

    private async Task<bool> HasAnotherEffectiveSuperAdminAsync(Guid userId, CancellationToken ct)
    {
        var roleId = await GetSuperAdminRoleIdAsync(ct);
        var applicationId = await GetAuthCenterApplicationIdAsync(ct);
        return roleId.HasValue && applicationId.HasValue && await IsEffectiveSuperAdminQuery(roleId.Value, applicationId.Value)
            .AnyAsync(user => user.Id != userId, ct);
    }

    private async Task<bool> WouldRemoveLastSuperAdminAsync(
        Guid userId,
        Guid? applicationId,
        Guid? roleId,
        bool deactivateUser,
        CancellationToken ct)
    {
        if (!await IsEffectiveSuperAdminAsync(userId, ct) || await HasAnotherEffectiveSuperAdminAsync(userId, ct))
            return false;
        if (deactivateUser)
            return true;
        if (applicationId.HasValue)
        {
            var authCenterApplicationId = await GetAuthCenterApplicationIdAsync(ct);
            if (applicationId != authCenterApplicationId)
                return false;
            return !await HasInheritedApplicationAccessAsync(userId, applicationId.Value, ct);
        }
        if (roleId.HasValue)
        {
            var superAdminRoleId = await GetSuperAdminRoleIdAsync(ct);
            return roleId == superAdminRoleId;
        }
        return false;
    }

    private Task<bool> CurrentUserIsEffectiveSuperAdminAsync(CancellationToken ct) =>
        _currentUser.UserId.HasValue ? IsEffectiveSuperAdminAsync(_currentUser.UserId.Value, ct) : Task.FromResult(false);

    private IQueryable<ApplicationUser> IsEffectiveSuperAdminQuery(Guid roleId, Guid applicationId) => _db.Users.Where(user => user.IsActive &&
        user.ApplicationAccesses.Any(access => access.ApplicationSystemId == applicationId && access.IsActive && access.ApplicationSystem.IsActive) &&
        _db.UserRoles.Any(userRole => userRole.UserId == user.Id && userRole.RoleId == roleId));

    private void AddAudit(string action, Guid subjectUserId, object? metadata = null) => _db.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(),
        UserId = _currentUser.UserId,
        Action = action,
        EntityName = nameof(ApplicationUser),
        EntityId = subjectUserId.ToString(),
        MetadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata),
        CreatedAt = _dateTimeProvider.UtcNow
    });

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
