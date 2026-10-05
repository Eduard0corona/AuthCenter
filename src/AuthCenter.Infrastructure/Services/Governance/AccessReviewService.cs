using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Governance;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Governance;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services.Governance;

/// <summary>
/// Access reviews: a campaign captures who can use an application (directly or through groups)
/// and its owners, or governance administrators, keep or revoke each access. A revocation removes
/// the direct access at once; access that comes from a group stays until someone removes the
/// membership, and the item says so. At the due date the campaign closes: what nobody reviewed is
/// kept or revoked, as the campaign says, and a recurring campaign starts again later.
/// </summary>
public sealed class AccessReviewService : IAccessReviewService
{
    private const int MaxTextLength = 1000;
    private static readonly TimeSpan ClaimLifetime = TimeSpan.FromMinutes(5);

    private readonly AuthCenterDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly IUserAccessService _userAccess;
    private readonly IEmailService _email;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<AccessReviewService> _logger;
    private readonly string _origin;

    public AccessReviewService(
        AuthCenterDbContext db,
        IDateTimeProvider clock,
        IUserAccessService userAccess,
        IEmailService email,
        ICurrentUserService currentUser,
        IOptions<JwtSettings> jwt,
        ILogger<AccessReviewService> logger)
    {
        _db = db;
        _clock = clock;
        _userAccess = userAccess;
        _email = email;
        _currentUser = currentUser;
        _logger = logger;
        _origin = jwt.Value.Issuer.TrimEnd('/');
    }

    public async Task<PagedResult<AccessReviewCampaignDto>> GetCampaignsAsync(AccessReviewQuery query, CancellationToken ct = default)
    {
        var campaigns = _db.AccessReviewCampaigns.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<AccessReviewStatus>(query.Status.Trim(), true, out var status) || !Enum.IsDefined(status))
                return PagedResult<AccessReviewCampaignDto>.Create([], 0, query.Page, query.PageSize);
            campaigns = campaigns.Where(campaign => campaign.Status == status);
        }
        if (query.ApplicationSystemId is { } applicationSystemId)
            campaigns = campaigns.Where(campaign => campaign.ApplicationSystemId == applicationSystemId);
        var total = await campaigns.CountAsync(ct);
        var items = await Project(campaigns.OrderByDescending(campaign => campaign.CreatedAt).Skip(query.Skip).Take(query.PageSize)).ToListAsync(ct);
        return PagedResult<AccessReviewCampaignDto>.Create(items, total, query.Page, query.PageSize);
    }

    public async Task<AccessReviewCampaignDto?> GetCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await Project(_db.AccessReviewCampaigns.AsNoTracking().Where(item => item.Id == campaignId)).SingleOrDefaultAsync(ct);
        if (campaign is null)
            return null;
        var reviewers = await _db.ApplicationOwners.AsNoTracking()
            .Where(owner => owner.ApplicationSystemId == campaign.ApplicationSystemId)
            .OrderBy(owner => owner.User.FullName)
            .Select(owner => new GovernanceUserDto { Id = owner.UserId, FullName = owner.User.FullName, Email = owner.User.Email ?? string.Empty, IsActive = owner.User.IsActive })
            .ToListAsync(ct);
        return Copy(campaign, reviewers);
    }

    /// <summary>The active campaigns of the applications the user owns (account portal).</summary>
    public async Task<IReadOnlyList<AccessReviewCampaignDto>> GetActiveCampaignsForOwnerAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var owned = _db.ApplicationOwners.Where(owner => owner.UserId == ownerUserId).Select(owner => owner.ApplicationSystemId);
        return await Project(_db.AccessReviewCampaigns.AsNoTracking()
                .Where(campaign => campaign.Status == AccessReviewStatus.Active && owned.Contains(campaign.ApplicationSystemId))
                .OrderBy(campaign => campaign.DueAt))
            .ToListAsync(ct);
    }

    public async Task<OperationResult<AccessReviewCampaignDto>> CreateCampaignAsync(CreateAccessReviewRequest request, Guid? createdByUserId, CancellationToken ct = default)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 150)
            return Invalid("The name is required and at most 150 characters.");
        var now = _clock.UtcNow;
        var dueAt = request.DueAt.Kind == DateTimeKind.Local ? request.DueAt.ToUniversalTime() : DateTime.SpecifyKind(request.DueAt, DateTimeKind.Utc);
        if (dueAt < now.AddHours(1) || dueAt > now.AddYears(1))
            return Invalid("The due date is between one hour and one year from now.");
        if (request.RecurrenceMonths is < 1 or > 12)
            return Invalid("A recurring review repeats every 1 to 12 months.");
        var application = await _db.ApplicationSystems.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.ApplicationSystemId, ct);
        if (application is null)
            return OperationResult<AccessReviewCampaignDto>.Failure("APP_NOT_FOUND", "Application not found.");
        if (!application.IsActive)
            return OperationResult<AccessReviewCampaignDto>.Failure("APP_INACTIVE", "Inactive applications are not reviewed.");
        if (await _db.AccessReviewCampaigns.AnyAsync(campaign => campaign.ApplicationSystemId == application.Id && campaign.Status == AccessReviewStatus.Active, ct))
            return OperationResult<AccessReviewCampaignDto>.Failure("ACCESS_REVIEW_ACTIVE_EXISTS", "The application already has an active review; complete or cancel it first.");

        var campaign = await StartAsync(name, application, dueAt, request.RevokeUnreviewed, request.RecurrenceMonths, null, createdByUserId, ct);
        return OperationResult<AccessReviewCampaignDto>.Success((await GetCampaignAsync(campaign.Id, ct))!);
    }

    public async Task<OperationResult<AccessReviewCampaignDto>> CancelCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await _db.AccessReviewCampaigns.Include(item => item.ApplicationSystem).SingleOrDefaultAsync(item => item.Id == campaignId, ct);
        if (campaign is null)
            return OperationResult<AccessReviewCampaignDto>.Failure("ACCESS_REVIEW_NOT_FOUND", "Access review not found.");
        if (campaign.Status != AccessReviewStatus.Active)
            return OperationResult<AccessReviewCampaignDto>.Failure("ACCESS_REVIEW_NOT_ACTIVE", "Only an active review can be cancelled.");
        campaign.Status = AccessReviewStatus.Cancelled;
        campaign.CompletedAt = _clock.UtcNow;
        AddAudit("ACCESS_REVIEW_CANCELLED", campaign, new { campaign.Name });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return OperationResult<AccessReviewCampaignDto>.Failure(VersionedUpdates.ConflictCode, "The review changed meanwhile. Reload it and try again.");
        }
        return OperationResult<AccessReviewCampaignDto>.Success((await GetCampaignAsync(campaign.Id, ct))!);
    }

    public async Task<OperationResult<PagedResult<AccessReviewItemDto>>> GetItemsAsync(Guid campaignId, AccessReviewItemQuery query, GovernanceActor actor, CancellationToken ct = default)
    {
        var campaign = await _db.AccessReviewCampaigns.AsNoTracking().SingleOrDefaultAsync(item => item.Id == campaignId, ct);
        if (campaign is null)
            return OperationResult<PagedResult<AccessReviewItemDto>>.Failure("ACCESS_REVIEW_NOT_FOUND", "Access review not found.");
        if (!await CanReviewAsync(campaign, actor, ct))
            return OperationResult<PagedResult<AccessReviewItemDto>>.Failure("ACCESS_REVIEW_FORBIDDEN", "Only the application's owners review its access.");

        var items = _db.AccessReviewItems.AsNoTracking().Where(item => item.CampaignId == campaignId);
        if (!string.IsNullOrWhiteSpace(query.Decision))
        {
            if (!Enum.TryParse<AccessReviewDecision>(query.Decision.Trim(), true, out var decision) || !Enum.IsDefined(decision))
                return OperationResult<PagedResult<AccessReviewItemDto>>.Success(PagedResult<AccessReviewItemDto>.Create([], 0, query.Page, query.PageSize));
            items = items.Where(item => item.Decision == decision);
        }
        if (query.RemediationRequired is { } remediation)
            items = items.Where(item => item.RemediationRequired == remediation);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            items = items.Where(item => item.User.FullName.Contains(search) || (item.User.Email != null && item.User.Email.Contains(search)));
        }
        var total = await items.CountAsync(ct);
        var active = campaign.Status == AccessReviewStatus.Active;
        var page = await ItemsAsync(items.OrderBy(item => item.User.FullName).ThenBy(item => item.Id).Skip(query.Skip).Take(query.PageSize), actor.UserId, active && actor.CanDecide, ct);
        return OperationResult<PagedResult<AccessReviewItemDto>>.Success(PagedResult<AccessReviewItemDto>.Create(page, total, query.Page, query.PageSize));
    }

    public async Task<OperationResult<AccessReviewItemDto>> DecideAsync(Guid campaignId, Guid itemId, DecideAccessReviewItemRequest request, GovernanceActor actor, CancellationToken ct = default)
    {
        if (!Enum.TryParse<AccessReviewDecision>(request.Decision?.Trim(), true, out var decision) || decision == AccessReviewDecision.Pending || !Enum.IsDefined(decision))
            return ItemFailure("ACCESS_REVIEW_INVALID", "The decision is Keep or Revoke.");
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (comment is { Length: > MaxTextLength })
            return ItemFailure("ACCESS_REVIEW_INVALID", $"The comment is at most {MaxTextLength} characters.");
        var campaign = await _db.AccessReviewCampaigns.AsNoTracking().Include(item => item.ApplicationSystem).SingleOrDefaultAsync(item => item.Id == campaignId, ct);
        if (campaign is null)
            return ItemFailure("ACCESS_REVIEW_NOT_FOUND", "Access review not found.");
        if (!await CanReviewAsync(campaign, actor, ct))
            return ItemFailure("ACCESS_REVIEW_FORBIDDEN", "Only the application's owners review its access.");
        if (campaign.Status != AccessReviewStatus.Active)
            return ItemFailure("ACCESS_REVIEW_NOT_ACTIVE", "The review is no longer active.");
        var item = await _db.AccessReviewItems.SingleOrDefaultAsync(entry => entry.Id == itemId && entry.CampaignId == campaignId, ct);
        if (item is null)
            return ItemFailure("ACCESS_REVIEW_ITEM_NOT_FOUND", "Review item not found.");
        if (item.UserId == actor.UserId)
            return ItemFailure("SELF_REVIEW_FORBIDDEN", "Nobody reviews their own access.");
        if (item.Decision != AccessReviewDecision.Pending)
            return ItemFailure("ACCESS_REVIEW_ITEM_DECIDED", "This access was already reviewed.");

        // The decision is recorded first (one reviewer wins), then carried out.
        item.Decision = decision;
        item.DecidedAt = _clock.UtcNow;
        item.DecidedByUserId = actor.UserId;
        item.Comment = comment;
        item.Outcome = decision == AccessReviewDecision.Keep ? "Access kept." : null;
        AddAudit("ACCESS_REVIEW_DECIDED", campaign, new { itemId = item.Id, userId = item.UserId, decision = decision.ToString(), decidedAs = actor.IsAdministrator ? "administrator" : "owner" }, actor.UserId);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ItemFailure("ACCESS_REVIEW_ITEM_DECIDED", "Someone else reviewed this access first.");
        }
        if (decision == AccessReviewDecision.Revoke)
            await RevokeAsync(item.Id, campaign.ApplicationSystemId, ct);

        var decided = await ItemsAsync(_db.AccessReviewItems.AsNoTracking().Where(entry => entry.Id == item.Id), actor.UserId, active: true, ct);
        return OperationResult<AccessReviewItemDto>.Success(decided.Single());
    }

    public async Task<int> RunDueWorkAsync(CancellationToken ct = default)
    {
        var processed = 0;
        var now = _clock.UtcNow;
        var due = await _db.AccessReviewCampaigns.AsNoTracking()
            .Where(campaign => campaign.Status == AccessReviewStatus.Active && campaign.DueAt <= now && (campaign.LockedUntil == null || campaign.LockedUntil < now))
            .OrderBy(campaign => campaign.DueAt)
            .Select(campaign => campaign.Id)
            .Take(20)
            .ToListAsync(ct);
        foreach (var campaignId in due)
        {
            if (!await ClaimAsync(campaignId, ct))
                continue;
            await CompleteAsync(campaignId, ct);
            processed++;
        }

        var recurring = await _db.AccessReviewCampaigns.AsNoTracking()
            .Where(campaign => campaign.Status == AccessReviewStatus.Completed && campaign.RecurrenceMonths != null && !campaign.NextStarted &&
                (campaign.LockedUntil == null || campaign.LockedUntil < now))
            .Select(campaign => new { campaign.Id, campaign.CreatedAt, campaign.RecurrenceMonths })
            .ToListAsync(ct);
        foreach (var campaign in recurring.Where(campaign => campaign.CreatedAt.AddMonths(campaign.RecurrenceMonths!.Value) <= now).Take(20))
        {
            if (!await ClaimAsync(campaign.Id, ct))
                continue;
            await StartNextAsync(campaign.Id, ct);
            processed++;
        }
        return processed;
    }

    private async Task<AccessReviewCampaign> StartAsync(
        string name, ApplicationSystem application, DateTime dueAt, bool revokeUnreviewed, int? recurrenceMonths, Guid? previousCampaignId, Guid? createdByUserId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var campaign = new AccessReviewCampaign
        {
            Id = Guid.NewGuid(),
            Name = name,
            ApplicationSystemId = application.Id,
            Status = AccessReviewStatus.Active,
            CreatedAt = now,
            CreatedByUserId = createdByUserId,
            DueAt = dueAt,
            RevokeUnreviewed = revokeUnreviewed,
            RecurrenceMonths = recurrenceMonths,
            PreviousCampaignId = previousCampaignId
        };
        _db.AccessReviewCampaigns.Add(campaign);
        foreach (var snapshot in await SnapshotAsync(application.Id, ct))
        {
            _db.AccessReviewItems.Add(new AccessReviewItem
            {
                Id = Guid.NewGuid(),
                CampaignId = campaign.Id,
                UserId = snapshot.UserId,
                HasDirectAccess = snapshot.Direct,
                GroupNames = Join(snapshot.Groups),
                RoleNames = Join(snapshot.Roles),
                Decision = AccessReviewDecision.Pending
            });
        }
        AddAudit("ACCESS_REVIEW_STARTED", campaign, new { name, dueAt, revokeUnreviewed, recurrenceMonths, previousCampaignId }, createdByUserId, application.Code);
        await _db.SaveChangesAsync(ct);

        var items = await _db.AccessReviewItems.CountAsync(item => item.CampaignId == campaign.Id, ct);
        var owners = await _db.ApplicationOwners.AsNoTracking()
            .Where(owner => owner.ApplicationSystemId == application.Id && owner.User.IsActive && owner.User.Email != null)
            .Select(owner => new { owner.User.Email, owner.User.FullName })
            .ToListAsync(ct);
        var applicationName = await EmailBranding.NameAsync(_db, application.Id, ct);
        foreach (var owner in owners)
        {
            await _email.SendNotificationAsync(owner.Email!, owner.FullName, $"Revisión de accesos de {applicationName}",
                $"Revisa quién tiene acceso a {applicationName} ({(items == 1 ? "1 acceso" : $"{items} accesos")}) antes del {dueAt:yyyy-MM-dd HH:mm} UTC. " +
                $"Los accesos que nadie revise para entonces se {(revokeUnreviewed ? "revocarán" : "conservarán")}.",
                $"{_origin}/portal#approvals", "Empezar la revisión", ct);
        }
        return campaign;
    }

    /// <summary>Active users who can use the application now: directly, through active groups, or both.</summary>
    private async Task<List<Snapshot>> SnapshotAsync(Guid applicationSystemId, CancellationToken ct)
    {
        var direct = await _db.UserApplicationAccesses.AsNoTracking()
            .Where(access => access.ApplicationSystemId == applicationSystemId && access.IsActive && access.User.IsActive)
            .Select(access => access.UserId)
            .ToListAsync(ct);
        var viaGroups = await _db.UserGroupMemberships.AsNoTracking()
            .Where(membership => membership.Group.IsActive && membership.User.IsActive &&
                membership.Group.ApplicationAssignments.Any(assignment => assignment.ApplicationSystemId == applicationSystemId))
            .Select(membership => new { membership.UserId, membership.Group.Name })
            .ToListAsync(ct);
        var directRoles = await _db.UserRoles.AsNoTracking()
            .Join(_db.Roles.Where(role => role.ApplicationSystemId == applicationSystemId && role.IsActive), userRole => userRole.RoleId, role => role.Id,
                (userRole, role) => new { userRole.UserId, role.DisplayName })
            .ToListAsync(ct);
        var groupRoles = await _db.UserGroupMemberships.AsNoTracking()
            .Where(membership => membership.Group.IsActive && membership.Group.ApplicationAssignments.Any(assignment => assignment.ApplicationSystemId == applicationSystemId))
            .SelectMany(membership => membership.Group.RoleAssignments
                .Where(assignment => assignment.Role.IsActive && assignment.Role.ApplicationSystemId == applicationSystemId)
                .Select(assignment => new { membership.UserId, assignment.Role.DisplayName }))
            .ToListAsync(ct);

        // Indexed by user: the campaign of an application with 100,000 users stays linear.
        var directSet = direct.ToHashSet();
        var groupsByUser = viaGroups.ToLookup(entry => entry.UserId, entry => entry.Name);
        var rolesByUser = directRoles.Concat(groupRoles).ToLookup(entry => entry.UserId, entry => entry.DisplayName);
        return direct.Concat(viaGroups.Select(entry => entry.UserId))
            .Distinct()
            .Select(userId => new Snapshot(
                userId,
                directSet.Contains(userId),
                groupsByUser[userId].Distinct().Order().ToList(),
                rolesByUser[userId].Distinct().Order().ToList()))
            .ToList();
    }

    /// <summary>Carries out a revocation: the direct access goes; access through groups is left for someone to remove.</summary>
    private async Task RevokeAsync(Guid itemId, Guid applicationSystemId, CancellationToken ct)
    {
        var target = await _db.AccessReviewItems.AsNoTracking().SingleAsync(item => item.Id == itemId, ct);
        var outcome = new List<string>();
        var remediation = false;
        if (target.HasDirectAccess)
        {
            var revoked = await _userAccess.RevokeAccessAsync(target.UserId, applicationSystemId, ct);
            if (revoked.IsSuccess)
                outcome.Add("Direct access revoked.");
            else if (revoked.ErrorCode == "ACCESS_NOT_FOUND")
                outcome.Add("The direct access no longer existed.");
            else
            {
                remediation = true;
                outcome.Add($"The direct access could not be revoked: {revoked.Message}");
            }
        }
        // Whatever still grants the application through groups, now.
        var groups = await _db.UserGroupMemberships.AsNoTracking()
            .Where(membership => membership.UserId == target.UserId && membership.Group.IsActive &&
                membership.Group.ApplicationAssignments.Any(assignment => assignment.ApplicationSystemId == applicationSystemId))
            .Select(membership => membership.Group.Name)
            .OrderBy(name => name)
            .ToListAsync(ct);
        if (groups.Count > 0)
        {
            remediation = true;
            outcome.Add($"Still granted through groups: {string.Join(", ", groups)}. Remove the membership to finish the revocation.");
        }

        // The revocation ran in its own unit of work; the item is read again to record it.
        var item = await _db.AccessReviewItems.SingleAsync(entry => entry.Id == itemId, ct);
        item.Outcome = Truncate(string.Join(" ", outcome.DefaultIfEmpty("Nothing left to revoke.")));
        item.RemediationRequired = remediation;
        await _db.SaveChangesAsync(ct);
    }

    private async Task CompleteAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await _db.AccessReviewCampaigns.AsNoTracking().Include(item => item.ApplicationSystem).SingleAsync(item => item.Id == campaignId, ct);
        var decision = campaign.RevokeUnreviewed ? AccessReviewDecision.Revoke : AccessReviewDecision.Keep;
        while (true)
        {
            if (!await RenewClaimAsync(campaignId, ct))
                return;
            var pending = await _db.AccessReviewItems
                .Where(item => item.CampaignId == campaignId && item.Decision == AccessReviewDecision.Pending)
                .Take(200)
                .ToListAsync(ct);
            if (pending.Count == 0)
                break;
            var now = _clock.UtcNow;
            foreach (var item in pending)
            {
                item.Decision = decision;
                item.DecidedAt = now;
                item.DecidedAutomatically = true;
                item.Outcome = decision == AccessReviewDecision.Keep ? "Not reviewed by the due date: access kept." : null;
            }
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            if (decision == AccessReviewDecision.Revoke)
            {
                foreach (var item in pending)
                    await RevokeAsync(item.Id, campaign.ApplicationSystemId, ct);
                _db.ChangeTracker.Clear();
            }
        }

        // Revocations decided but never carried out (an instance stopped halfway) are finished now.
        var unfinished = await _db.AccessReviewItems.AsNoTracking()
            .Where(item => item.CampaignId == campaignId && item.Decision == AccessReviewDecision.Revoke && item.Outcome == null)
            .Select(item => item.Id)
            .ToListAsync(ct);
        foreach (var batch in unfinished.Chunk(200))
        {
            if (!await RenewClaimAsync(campaignId, ct))
                return;
            foreach (var itemId in batch)
                await RevokeAsync(itemId, campaign.ApplicationSystemId, ct);
            _db.ChangeTracker.Clear();
        }

        var completed = await _db.AccessReviewCampaigns.SingleAsync(item => item.Id == campaignId, ct);
        completed.Status = AccessReviewStatus.Completed;
        completed.CompletedAt = _clock.UtcNow;
        completed.LockedUntil = null;
        var counts = await _db.AccessReviewItems.AsNoTracking().Where(item => item.CampaignId == campaignId)
            .GroupBy(item => item.Decision)
            .Select(group => new { Decision = group.Key, Count = group.Count() })
            .ToListAsync(ct);
        AddAudit("ACCESS_REVIEW_COMPLETED", completed, new
        {
            kept = counts.Where(entry => entry.Decision == AccessReviewDecision.Keep).Sum(entry => entry.Count),
            revoked = counts.Where(entry => entry.Decision == AccessReviewDecision.Revoke).Sum(entry => entry.Count),
            unreviewedDecision = decision.ToString()
        }, null, campaign.ApplicationSystem.Code);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Access review {CampaignId} completed.", campaignId);
    }

    private async Task StartNextAsync(Guid campaignId, CancellationToken ct)
    {
        var previous = await _db.AccessReviewCampaigns.Include(item => item.ApplicationSystem).SingleAsync(item => item.Id == campaignId, ct);
        previous.NextStarted = true;
        previous.LockedUntil = null;
        var application = previous.ApplicationSystem;
        var hasActive = await _db.AccessReviewCampaigns.AnyAsync(item => item.ApplicationSystemId == application.Id && item.Status == AccessReviewStatus.Active, ct);
        if (!application.IsActive || hasActive)
        {
            // An inactive application is not reviewed again; one with a review in course keeps that one.
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Recurring access review {CampaignId} was not repeated (inactive application or review in course).", campaignId);
            return;
        }
        var duration = previous.DueAt - previous.CreatedAt;
        await StartAsync(previous.Name, application, _clock.UtcNow.Add(duration), previous.RevokeUnreviewed, previous.RecurrenceMonths, previous.Id, null, ct);
    }

    /// <summary>
    /// Extends the claim before each batch of a long completion (thousands of revocations outlast
    /// the claim). False when the campaign changed meanwhile, such as another instance claiming it:
    /// this one stops and leaves the rest to whoever holds it.
    /// </summary>
    private async Task<bool> RenewClaimAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await _db.AccessReviewCampaigns.SingleAsync(item => item.Id == campaignId, ct);
        campaign.LockedUntil = _clock.UtcNow.Add(ClaimLifetime);
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogWarning("Access review {CampaignId} changed while it was being completed; this instance stops.", campaignId);
            return false;
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>One instance at a time completes a campaign or starts its successor.</summary>
    private async Task<bool> ClaimAsync(Guid campaignId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var campaign = await _db.AccessReviewCampaigns.SingleOrDefaultAsync(item => item.Id == campaignId, ct);
        if (campaign is null || campaign.LockedUntil >= now)
            return false;
        campaign.LockedUntil = now.Add(ClaimLifetime);
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    private async Task<bool> CanReviewAsync(AccessReviewCampaign campaign, GovernanceActor actor, CancellationToken ct) =>
        actor.IsAdministrator ||
        await _db.ApplicationOwners.AnyAsync(owner => owner.UserId == actor.UserId && owner.ApplicationSystemId == campaign.ApplicationSystemId, ct);

    /// <summary>The items as the caller sees them, in the order of the query.</summary>
    private async Task<List<AccessReviewItemDto>> ItemsAsync(IQueryable<AccessReviewItem> items, Guid actorUserId, bool active, CancellationToken ct)
    {
        var rows = await items.Select(item => new
        {
            item.Id,
            item.CampaignId,
            item.UserId,
            item.User.FullName,
            item.User.Email,
            item.User.IsActive,
            item.HasDirectAccess,
            item.GroupNames,
            item.RoleNames,
            item.Decision,
            item.DecidedAt,
            item.DecidedByUserId,
            item.DecidedAutomatically,
            item.Comment,
            item.Outcome,
            item.RemediationRequired,
            item.Version
        }).ToListAsync(ct);
        var deciderIds = rows.Where(row => row.DecidedByUserId.HasValue).Select(row => row.DecidedByUserId!.Value).Distinct().ToList();
        var deciders = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(user => deciderIds.Contains(user.Id))
            .Select(user => new GovernanceUserDto { Id = user.Id, FullName = user.FullName, Email = user.Email ?? string.Empty, IsActive = user.IsActive })
            .ToDictionaryAsync(user => user.Id, ct);
        return rows.Select(row => new AccessReviewItemDto
        {
            Id = row.Id,
            CampaignId = row.CampaignId,
            User = new GovernanceUserDto { Id = row.UserId, FullName = row.FullName, Email = row.Email ?? string.Empty, IsActive = row.IsActive },
            HasDirectAccess = row.HasDirectAccess,
            Groups = Split(row.GroupNames),
            Roles = Split(row.RoleNames),
            Decision = row.Decision.ToString(),
            DecidedAt = row.DecidedAt,
            DecidedBy = row.DecidedByUserId is { } deciderId && deciders.TryGetValue(deciderId, out var decider) ? decider : null,
            DecidedAutomatically = row.DecidedAutomatically,
            Comment = row.Comment,
            Outcome = row.Outcome,
            RemediationRequired = row.RemediationRequired,
            CanDecide = active && row.Decision == AccessReviewDecision.Pending && row.UserId != actorUserId,
            Version = row.Version
        }).ToList();
    }

    private static IQueryable<AccessReviewCampaignDto> Project(IQueryable<AccessReviewCampaign> campaigns) => campaigns.Select(campaign => new AccessReviewCampaignDto
    {
        Id = campaign.Id,
        Name = campaign.Name,
        ApplicationSystemId = campaign.ApplicationSystemId,
        ApplicationCode = campaign.ApplicationSystem.Code,
        ApplicationName = campaign.ApplicationSystem.Name,
        Status = campaign.Status.ToString(),
        CreatedAt = campaign.CreatedAt,
        DueAt = campaign.DueAt,
        CompletedAt = campaign.CompletedAt,
        RevokeUnreviewed = campaign.RevokeUnreviewed,
        RecurrenceMonths = campaign.RecurrenceMonths,
        PreviousCampaignId = campaign.PreviousCampaignId,
        TotalItems = campaign.Items.Count(),
        PendingItems = campaign.Items.Count(item => item.Decision == AccessReviewDecision.Pending),
        KeptItems = campaign.Items.Count(item => item.Decision == AccessReviewDecision.Keep),
        RevokedItems = campaign.Items.Count(item => item.Decision == AccessReviewDecision.Revoke),
        RemediationItems = campaign.Items.Count(item => item.RemediationRequired),
        Version = campaign.Version
    });

    private static AccessReviewCampaignDto Copy(AccessReviewCampaignDto campaign, IReadOnlyList<GovernanceUserDto> reviewers) => new()
    {
        Id = campaign.Id,
        Name = campaign.Name,
        ApplicationSystemId = campaign.ApplicationSystemId,
        ApplicationCode = campaign.ApplicationCode,
        ApplicationName = campaign.ApplicationName,
        Status = campaign.Status,
        CreatedAt = campaign.CreatedAt,
        DueAt = campaign.DueAt,
        CompletedAt = campaign.CompletedAt,
        RevokeUnreviewed = campaign.RevokeUnreviewed,
        RecurrenceMonths = campaign.RecurrenceMonths,
        PreviousCampaignId = campaign.PreviousCampaignId,
        TotalItems = campaign.TotalItems,
        PendingItems = campaign.PendingItems,
        KeptItems = campaign.KeptItems,
        RevokedItems = campaign.RevokedItems,
        RemediationItems = campaign.RemediationItems,
        Reviewers = reviewers,
        Version = campaign.Version
    };

    private void AddAudit(string action, AccessReviewCampaign campaign, object metadata, Guid? actorUserId = null, string? applicationCode = null) =>
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = actorUserId ?? _currentUser.UserId,
            ApplicationCode = applicationCode ?? campaign.ApplicationSystem?.Code,
            Action = action,
            EntityName = nameof(AccessReviewCampaign),
            EntityId = campaign.Id.ToString(),
            MetadataJson = JsonSerializer.Serialize(metadata),
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToHexString(),
            CreatedAt = _clock.UtcNow
        });

    private static string? Join(IReadOnlyList<string> values) => values.Count == 0 ? null : Truncate(string.Join(", ", values));

    private static List<string> Split(string? value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string Truncate(string value) => value.Length <= MaxTextLength ? value : value[..(MaxTextLength - 1)] + "…";

    private static OperationResult<AccessReviewCampaignDto> Invalid(string message) =>
        OperationResult<AccessReviewCampaignDto>.Failure("ACCESS_REVIEW_INVALID", message);

    private static OperationResult<AccessReviewItemDto> ItemFailure(string code, string message) =>
        OperationResult<AccessReviewItemDto>.Failure(code, message);

    private sealed record Snapshot(Guid UserId, bool Direct, IReadOnlyList<string> Groups, IReadOnlyList<string> Roles);
}
