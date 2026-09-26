using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Governance;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Governance;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services.Governance;

public sealed partial class AccessGovernanceService
{
    private const int MaxBatches = 50;

    public async Task<PagedResult<AccessRequestDto>> GetRequestsAsync(AccessRequestQuery query, CancellationToken ct = default)
    {
        var requests = _db.AccessRequests.AsNoTracking();
        if (Text(query.Status) is { } statusText)
        {
            if (!Enum.TryParse<AccessRequestStatus>(statusText, true, out var status) || !Enum.IsDefined(status))
                return PagedResult<AccessRequestDto>.Create([], 0, query.Page, query.PageSize);
            requests = requests.Where(request => request.Status == status);
        }
        if (query.ApplicationSystemId is { } applicationSystemId)
            requests = requests.Where(request => request.ApplicationSystemId == applicationSystemId);
        if (query.UserId is { } userId)
            requests = requests.Where(request => request.UserId == userId);
        if (Text(query.Search) is { } search)
            requests = requests.Where(request => request.User.FullName.Contains(search) || (request.User.Email != null && request.User.Email.Contains(search)));

        var total = await requests.CountAsync(ct);
        var items = await Project(requests)
            .OrderByDescending(request => request.CreatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(ct);
        return PagedResult<AccessRequestDto>.Create(items, total, query.Page, query.PageSize);
    }

    public Task<AccessRequestDto?> GetRequestAsync(Guid requestId, CancellationToken ct = default) =>
        Project(_db.AccessRequests.AsNoTracking().Where(request => request.Id == requestId)).SingleOrDefaultAsync(ct);

    public Task<OperationResult<AccessRequestDto>> ApproveAsync(Guid requestId, GovernanceActor actor, string? comment, CancellationToken ct = default) =>
        _db.RunRetriableAsync(() => DecideAsync(requestId, actor, comment, approve: true, ct));

    public Task<OperationResult<AccessRequestDto>> RejectAsync(Guid requestId, GovernanceActor actor, string? comment, CancellationToken ct = default) =>
        _db.RunRetriableAsync(() => DecideAsync(requestId, actor, comment, approve: false, ct));

    public async Task<OperationResult> ApprovePendingAccessAsync(Guid userId, Guid applicationSystemId, GovernanceActor actor, CancellationToken ct = default)
    {
        var access = await _db.UserApplicationAccesses.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.ApplicationSystemId == applicationSystemId, ct);
        if (access is null)
            return OperationResult.Failure("ACCESS_NOT_FOUND", "User does not have pending access for this application.");
        if (access.IsActive)
            return OperationResult.Failure("ACCESS_ALREADY_ACTIVE", "User access is already active for this application.");
        // A revoked access is not waiting for anyone: granting it again is a separate, explicit act.
        if (access.RevokedAt is not null)
            return OperationResult.Failure("ACCESS_NOT_PENDING", "The access was revoked, not requested. Grant it again instead.");

        var requestId = await _db.AccessRequests
            .Where(request => request.UserId == userId && request.ApplicationSystemId == applicationSystemId && request.Status == AccessRequestStatus.Pending)
            .OrderByDescending(request => request.CreatedAt)
            .Select(request => (Guid?)request.Id)
            .FirstOrDefaultAsync(ct);
        if (requestId is { } id)
        {
            var decided = await ApproveAsync(id, actor, null, ct);
            return decided.IsSuccess ? OperationResult.Success() : OperationResult.Failure(decided.ErrorCode, decided.Message);
        }

        // Pending access recorded before access requests existed.
        if (userId == actor.UserId)
            return OperationResult.Failure("SELF_APPROVAL_FORBIDDEN", "Nobody approves their own access.");
        var pending = await _db.UserApplicationAccesses.Include(item => item.ApplicationSystem)
            .SingleAsync(item => item.UserId == userId && item.ApplicationSystemId == applicationSystemId, ct);
        pending.IsActive = true;
        pending.RevokedAt = null;
        AddAudit("USER_APPLICATION_ACCESS_APPROVED", nameof(ApplicationUser), userId, pending.ApplicationSystem.Code, new { applicationSystemId }, actor.UserId);
        await _db.SaveChangesAsync(ct);
        await _refreshTokens.RevokeAllForUserAsync(userId, pending.ApplicationSystem.Code, ct);
        return OperationResult.Success();
    }

    public async Task<IReadOnlyList<RequestableApplicationDto>> GetRequestableApplicationsAsync(Guid userId, CancellationToken ct = default)
    {
        var applications = await _db.ApplicationGovernance.AsNoTracking()
            .Where(settings => settings.AccessRequestsEnabled && settings.ApplicationSystem.IsActive)
            .Select(settings => new { settings.ApplicationSystem.Id, settings.ApplicationSystem.Code, settings.ApplicationSystem.Name, settings.ApplicationSystem.Description })
            .OrderBy(application => application.Name)
            .ToListAsync(ct);
        if (applications.Count == 0)
            return [];
        var ids = applications.Select(application => application.Id).ToList();
        var roles = await _db.Roles.AsNoTracking()
            .Where(role => role.ApplicationSystemId != null && ids.Contains(role.ApplicationSystemId.Value) && role.IsActive && !role.IsSystemRole)
            .OrderBy(role => role.DisplayName)
            .Select(role => new { role.Id, role.DisplayName, role.Description, ApplicationSystemId = role.ApplicationSystemId!.Value })
            .ToListAsync(ct);
        var accessible = await AccessibleApplicationIdsAsync(userId, ids, ct);
        return applications.Select(application => new RequestableApplicationDto
        {
            Id = application.Id,
            Code = application.Code,
            Name = application.Name,
            Description = application.Description,
            HasAccess = accessible.Contains(application.Id),
            Roles = roles.Where(role => role.ApplicationSystemId == application.Id)
                .Select(role => new RequestableRoleDto { Id = role.Id, Name = role.DisplayName, Description = role.Description })
                .ToList()
        }).ToList();
    }

    public async Task<IReadOnlyList<AccessRequestDto>> GetUserRequestsAsync(Guid userId, CancellationToken ct = default) =>
        await Project(_db.AccessRequests.AsNoTracking().Where(request => request.UserId == userId))
            .OrderByDescending(request => request.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

    public Task<OperationResult<AccessRequestDto>> CreateRequestAsync(Guid userId, CreateAccessRequestRequest request, CancellationToken ct = default) =>
        _db.RunRetriableAsync(() => CreateRequestCoreAsync(userId, request, ct));

    private async Task<OperationResult<AccessRequestDto>> CreateRequestCoreAsync(Guid userId, CreateAccessRequestRequest request, CancellationToken ct)
    {
        var justification = Text(request.Justification);
        if (justification is null)
            return OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_INVALID", "Say why you need the access.");
        if (justification.Length > MaxTextLength)
            return OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_INVALID", $"The justification is at most {MaxTextLength} characters.");
        var user = await _db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId, ct);
        if (user is null || !user.IsActive)
            return OperationResult<AccessRequestDto>.Failure("USER_INACTIVE", "Your account is inactive.");
        var application = await _db.ApplicationGovernance.AsNoTracking()
            .Where(settings => settings.ApplicationSystemId == request.ApplicationSystemId && settings.AccessRequestsEnabled && settings.ApplicationSystem.IsActive)
            .Select(settings => settings.ApplicationSystem)
            .SingleOrDefaultAsync(ct);
        if (application is null)
            return OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_NOT_ALLOWED", "This application does not take access requests.");
        ApplicationRole? role = null;
        if (request.RoleId is { } roleId)
        {
            role = await _db.Roles.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == roleId && item.ApplicationSystemId == application.Id && item.IsActive && !item.IsSystemRole, ct);
            if (role is null)
                return OperationResult<AccessRequestDto>.Failure("ROLE_INVALID", "That role cannot be requested for this application.");
        }
        if (role is null && (await AccessibleApplicationIdsAsync(userId, [application.Id], ct)).Count > 0)
            return OperationResult<AccessRequestDto>.Failure("ACCESS_ALREADY_ACTIVE", "You already have access to this application.");
        if (role is not null && await HoldsRoleAsync(userId, role.Id, ct))
            return OperationResult<AccessRequestDto>.Failure("ROLE_ALREADY_ASSIGNED", "You already have this role.");
        if (await _db.AccessRequests.AnyAsync(item => item.UserId == userId && item.ApplicationSystemId == application.Id && item.Status == AccessRequestStatus.Pending, ct))
            return OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_EXISTS", "A request for this application is already waiting for a decision.");
        if (await _db.AccessRequests.CountAsync(item => item.UserId == userId && item.Status == AccessRequestStatus.Pending, ct) >= _settings.PendingRequestLimit)
            return OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_LIMIT", $"You have {_settings.PendingRequestLimit} requests waiting; wait for a decision before asking for more.");

        await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
        var now = _clock.UtcNow;
        var created = new AccessRequest
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ApplicationSystemId = application.Id,
            RequestedRoleId = role?.Id,
            Source = AccessRequestSource.Portal,
            Status = AccessRequestStatus.Pending,
            Justification = justification,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_settings.RequestLifetimeDays)
        };
        _db.AccessRequests.Add(created);
        AddAudit("ACCESS_REQUEST_CREATED", nameof(AccessRequest), created.Id, application.Code,
            new { userId, applicationSystemId = application.Id, roleId = role?.Id, source = nameof(AccessRequestSource.Portal) }, userId);
        await _db.SaveChangesAsync(ct);
        var asked = role is null ? $"access to {application.Name}" : $"the role {role.DisplayName} in {application.Name}";
        await NotifyOwnersAsync(application.Id, userId, $"Access request for {application.Name}",
            $"{user.FullName} ({user.Email}) asks for {asked}: \"{justification}\"", ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return OperationResult<AccessRequestDto>.Success((await GetRequestAsync(created.Id, ct))!);
    }

    public Task<OperationResult<AccessRequestDto>> CancelRequestAsync(Guid userId, Guid requestId, CancellationToken ct = default) =>
        _db.RunRetriableAsync(async () =>
        {
            var request = await _db.AccessRequests.Include(item => item.ApplicationSystem)
                .SingleOrDefaultAsync(item => item.Id == requestId && item.UserId == userId, ct);
            if (request is null)
                return OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_NOT_FOUND", "Access request not found.");
            if (request.Status != AccessRequestStatus.Pending)
                return NotPending(request.Status);
            var now = _clock.UtcNow;
            request.Status = AccessRequestStatus.Cancelled;
            request.DecidedAt = now;
            request.DecidedByUserId = userId;
            if (request.Source != AccessRequestSource.Portal)
                await ClosePendingAccessAsync(request.UserId, request.ApplicationSystemId, now, ct);
            AddAudit("ACCESS_REQUEST_CANCELLED", nameof(AccessRequest), request.Id, request.ApplicationSystem.Code,
                new { userId, applicationSystemId = request.ApplicationSystemId }, userId);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_NOT_PENDING", "The request was decided meanwhile.");
            }
            return OperationResult<AccessRequestDto>.Success((await GetRequestAsync(request.Id, ct))!);
        });

    public async Task<IReadOnlyList<AccessRequestDto>> GetPendingRequestsForOwnerAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var owned = _db.ApplicationOwners.Where(owner => owner.UserId == ownerUserId).Select(owner => owner.ApplicationSystemId);
        return await Project(_db.AccessRequests.AsNoTracking().Where(request =>
                request.Status == AccessRequestStatus.Pending && request.UserId != ownerUserId &&
                owned.Contains(request.ApplicationSystemId) && (request.ExpiresAt == null || request.ExpiresAt > now)))
            .OrderBy(request => request.CreatedAt)
            .Take(100)
            .ToListAsync(ct);
    }

    public async Task OpenPendingRequestAsync(Guid userId, Guid applicationSystemId, AccessRequestSource source, CancellationToken ct = default)
    {
        if (await _db.AccessRequests.AnyAsync(request => request.UserId == userId && request.ApplicationSystemId == applicationSystemId && request.Status == AccessRequestStatus.Pending, ct) ||
            _db.AccessRequests.Local.Any(request => request.UserId == userId && request.ApplicationSystemId == applicationSystemId && request.Status == AccessRequestStatus.Pending))
            return;
        var application = await _db.ApplicationSystems.AsNoTracking().Include(item => item.RegistrationSettings)
            .SingleAsync(item => item.Id == applicationSystemId, ct);
        var user = await _db.Users.AsNoTracking().SingleAsync(item => item.Id == userId, ct);
        var request = new AccessRequest
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ApplicationSystemId = applicationSystemId,
            // A registration asks for the application's default role, which it already assigned.
            RequestedRoleId = source == AccessRequestSource.Registration ? application.RegistrationSettings?.DefaultRoleId : null,
            Source = source,
            Status = AccessRequestStatus.Pending,
            CreatedAt = _clock.UtcNow
        };
        _db.AccessRequests.Add(request);
        AddAudit("ACCESS_REQUEST_CREATED", nameof(AccessRequest), request.Id, application.Code,
            new { userId, applicationSystemId, roleId = request.RequestedRoleId, source = source.ToString() },
            source == AccessRequestSource.Registration ? userId : null);
        var detail = source == AccessRequestSource.Registration
            ? $"{user.FullName} ({user.Email}) registered in {application.Name} and waits for approval."
            : $"{user.FullName} ({user.Email}) was given access to {application.Name} pending approval.";
        await NotifyOwnersAsync(applicationSystemId, userId, $"Access request for {application.Name}", detail, ct);
    }

    public async Task<int> ExpireRequestsAsync(CancellationToken ct = default)
    {
        var expired = 0;
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            var now = _clock.UtcNow;
            var requests = await _db.AccessRequests.Include(request => request.ApplicationSystem)
                .Where(request => request.Status == AccessRequestStatus.Pending && request.ExpiresAt != null && request.ExpiresAt <= now)
                .OrderBy(request => request.ExpiresAt)
                .Take(100)
                .ToListAsync(ct);
            if (requests.Count == 0)
                break;
            foreach (var request in requests)
            {
                request.Status = AccessRequestStatus.Expired;
                request.DecidedAt = now;
                AddAudit("ACCESS_REQUEST_EXPIRED", nameof(AccessRequest), request.Id, request.ApplicationSystem.Code,
                    new { userId = request.UserId, applicationSystemId = request.ApplicationSystemId }, null);
            }
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another instance or a decision got there first; take the next batch as it is now.
                _db.ChangeTracker.Clear();
                continue;
            }
            var userIds = requests.Select(request => request.UserId).Distinct().ToList();
            var users = await _db.Users.AsNoTracking().Where(user => userIds.Contains(user.Id) && user.IsActive && user.Email != null)
                .Select(user => new { user.Id, user.Email, user.FullName })
                .ToListAsync(ct);
            foreach (var request in requests)
            {
                if (users.FirstOrDefault(user => user.Id == request.UserId) is { } user)
                    await _email.SendNotificationAsync(user.Email!, user.FullName, $"Access request for {request.ApplicationSystem.Name} expired",
                        $"Nobody decided your request for access to {request.ApplicationSystem.Name} in time. Request it again if you still need it.",
                        PortalUrl("applications"), "Open your account", ct);
            }
            expired += requests.Count;
            _db.ChangeTracker.Clear();
        }
        return expired;
    }

    private async Task<OperationResult<AccessRequestDto>> DecideAsync(Guid requestId, GovernanceActor actor, string? comment, bool approve, CancellationToken ct)
    {
        comment = Text(comment);
        if (comment is { Length: > MaxTextLength })
            return OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_INVALID", $"The comment is at most {MaxTextLength} characters.");
        if (!approve && comment is null)
            return OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_INVALID", "Say why the request is rejected; the requester will read it.");

        await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
        var request = await _db.AccessRequests.Include(item => item.ApplicationSystem).Include(item => item.RequestedRole)
            .SingleOrDefaultAsync(item => item.Id == requestId, ct);
        if (request is null)
            return OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_NOT_FOUND", "Access request not found.");
        if (request.UserId == actor.UserId)
            return OperationResult<AccessRequestDto>.Failure("SELF_APPROVAL_FORBIDDEN", "Nobody decides their own access request.");
        if (!actor.IsAdministrator && !await IsOwnerAsync(actor.UserId, request.ApplicationSystemId, ct))
            return OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_FORBIDDEN", "Only the application's owners decide its access requests.");
        if (request.Status != AccessRequestStatus.Pending)
            return NotPending(request.Status);
        var now = _clock.UtcNow;
        if (request.ExpiresAt <= now)
            return OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_EXPIRED", "The request expired; the user can ask again.");
        var user = await _db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.UserId, ct);
        var application = request.ApplicationSystem;
        var role = request.RequestedRole;

        if (approve)
        {
            if (user is null || !user.IsActive)
                return OperationResult<AccessRequestDto>.Failure("USER_INACTIVE", "The requester's account is inactive or deleted.");
            if (!application.IsActive)
                return OperationResult<AccessRequestDto>.Failure("APP_INACTIVE", "The application is inactive.");
            if (role is not null && (!role.IsActive || role.IsSystemRole || role.ApplicationSystemId != application.Id))
                return OperationResult<AccessRequestDto>.Failure("ROLE_INVALID", "The requested role can no longer be granted.");
            var holdsRole = role is not null && await _db.UserRoles.AnyAsync(item => item.UserId == request.UserId && item.RoleId == role.Id, ct);
            if (role is not null && !holdsRole && await _separationOfDuties.CheckUserAsync(request.UserId, [role.Id], null, ct) is { } conflict)
                return OperationResult<AccessRequestDto>.Failure(SeparationOfDutiesConflict.ErrorCode, conflict.Message, conflict.Details);

            var access = await _db.UserApplicationAccesses.SingleOrDefaultAsync(item => item.UserId == request.UserId && item.ApplicationSystemId == application.Id, ct);
            if (access is null)
                _db.UserApplicationAccesses.Add(new UserApplicationAccess { Id = Guid.NewGuid(), UserId = request.UserId, ApplicationSystemId = application.Id, IsActive = true, CreatedAt = now });
            else
            {
                access.IsActive = true;
                access.RevokedAt = null;
            }
            if (role is not null && !holdsRole)
                _db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = request.UserId, RoleId = role.Id });
            request.Status = AccessRequestStatus.Approved;
            AddAudit("USER_APPLICATION_ACCESS_APPROVED", nameof(ApplicationUser), request.UserId, application.Code,
                new { applicationSystemId = application.Id, accessRequestId = request.Id, roleId = role?.Id }, actor.UserId);
        }
        else
        {
            request.Status = AccessRequestStatus.Rejected;
            // Access a registration or an administrator left pending stops waiting.
            if (request.Source != AccessRequestSource.Portal)
                await ClosePendingAccessAsync(request.UserId, application.Id, now, ct);
        }
        request.DecidedAt = now;
        request.DecidedByUserId = actor.UserId;
        request.DecisionComment = comment;
        AddAudit(approve ? "ACCESS_REQUEST_APPROVED" : "ACCESS_REQUEST_REJECTED", nameof(AccessRequest), request.Id, application.Code, new
        {
            userId = request.UserId,
            applicationSystemId = application.Id,
            roleId = request.RequestedRoleId,
            source = request.Source.ToString(),
            decidedAs = actor.IsAdministrator ? "administrator" : "owner"
        }, actor.UserId);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_NOT_PENDING", "Someone else decided the request first.");
        }

        if (user is { IsActive: true, Email: { } email })
        {
            var asked = role is null ? $"access to {application.Name}" : $"the role {role.DisplayName} in {application.Name}";
            var (subject, detail) = approve
                ? ($"Access to {application.Name} approved", $"Your request for {asked} was approved{(comment is null ? "." : $": {comment}")}")
                : ($"Access to {application.Name} not approved", $"Your request for {asked} was not approved: {comment}");
            await _email.SendNotificationAsync(email, user.FullName, subject, detail, PortalUrl("applications"), "Open your account", ct);
        }
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        if (approve)
            await _refreshTokens.RevokeAllForUserAsync(request.UserId, application.Code, ct);
        return OperationResult<AccessRequestDto>.Success((await GetRequestAsync(request.Id, ct))!);
    }

    private async Task ClosePendingAccessAsync(Guid userId, Guid applicationSystemId, DateTime now, CancellationToken ct)
    {
        var pending = await _db.UserApplicationAccesses.SingleOrDefaultAsync(item =>
            item.UserId == userId && item.ApplicationSystemId == applicationSystemId && !item.IsActive && item.RevokedAt == null, ct);
        if (pending is not null)
            pending.RevokedAt = now;
    }

    private async Task NotifyOwnersAsync(Guid applicationSystemId, Guid requesterId, string subject, string detail, CancellationToken ct)
    {
        foreach (var (email, name) in await OwnersToNotifyAsync(applicationSystemId, requesterId, ct))
            await _email.SendNotificationAsync(email, name, subject, detail, PortalUrl("approvals"), "Review the request", ct);
    }

    /// <summary>Of these applications, the ones the user can use: direct access or an active group's.</summary>
    private async Task<HashSet<Guid>> AccessibleApplicationIdsAsync(Guid userId, IReadOnlyCollection<Guid> applicationIds, CancellationToken ct)
    {
        var direct = _db.UserApplicationAccesses
            .Where(access => access.UserId == userId && access.IsActive && applicationIds.Contains(access.ApplicationSystemId))
            .Select(access => access.ApplicationSystemId);
        var viaGroups = _db.UserGroupMemberships
            .Where(membership => membership.UserId == userId && membership.Group.IsActive)
            .SelectMany(membership => membership.Group.ApplicationAssignments)
            .Where(assignment => applicationIds.Contains(assignment.ApplicationSystemId))
            .Select(assignment => assignment.ApplicationSystemId);
        return (await direct.Union(viaGroups).ToListAsync(ct)).ToHashSet();
    }

    private async Task<bool> HoldsRoleAsync(Guid userId, Guid roleId, CancellationToken ct) =>
        await _db.UserRoles.AnyAsync(item => item.UserId == userId && item.RoleId == roleId, ct) ||
        await SeparationOfDutiesChecker.GroupRoles(_db, userId).AnyAsync(id => id == roleId, ct);

    private static OperationResult<AccessRequestDto> NotPending(AccessRequestStatus status) =>
        OperationResult<AccessRequestDto>.Failure("ACCESS_REQUEST_NOT_PENDING", $"The request is no longer pending ({status}).");

    private IQueryable<AccessRequestDto> Project(IQueryable<AccessRequest> requests) =>
        from request in requests
        join decider in _db.Users.IgnoreQueryFilters() on request.DecidedByUserId equals (Guid?)decider.Id into deciders
        from decider in deciders.DefaultIfEmpty()
        select new AccessRequestDto
        {
            Id = request.Id,
            Requester = new GovernanceUserDto { Id = request.UserId, FullName = request.User.FullName, Email = request.User.Email ?? string.Empty, IsActive = request.User.IsActive },
            ApplicationSystemId = request.ApplicationSystemId,
            ApplicationCode = request.ApplicationSystem.Code,
            ApplicationName = request.ApplicationSystem.Name,
            RoleId = request.RequestedRoleId,
            RoleName = request.RequestedRole != null ? request.RequestedRole.DisplayName : null,
            Source = request.Source.ToString(),
            Status = request.Status.ToString(),
            Justification = request.Justification,
            CreatedAt = request.CreatedAt,
            ExpiresAt = request.ExpiresAt,
            DecidedAt = request.DecidedAt,
            DecidedBy = decider == null ? null : new GovernanceUserDto { Id = decider.Id, FullName = decider.FullName, Email = decider.Email ?? string.Empty, IsActive = decider.IsActive },
            DecisionComment = request.DecisionComment,
            Version = request.Version
        };
}
