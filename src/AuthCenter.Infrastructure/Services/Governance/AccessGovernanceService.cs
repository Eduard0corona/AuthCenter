using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Governance;
using AuthCenter.Contracts.Responses.Governance;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services.Governance;

/// <summary>
/// Application owners and access requests. Owners decide the requests of their applications from
/// the portal; governance administrators decide any request from the console. Nobody decides their
/// own request.
/// </summary>
public sealed partial class AccessGovernanceService : IAccessGovernanceService
{
    private const int MaxOwners = 20;
    private const int MaxTextLength = 1000;

    private readonly AuthCenterDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly ISeparationOfDutiesChecker _separationOfDuties;
    private readonly IEmailService _email;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly ICurrentUserService _currentUser;
    private readonly GovernanceSettings _settings;
    private readonly string _origin;

    public AccessGovernanceService(
        AuthCenterDbContext db,
        IDateTimeProvider clock,
        ISeparationOfDutiesChecker separationOfDuties,
        IEmailService email,
        IRefreshTokenService refreshTokens,
        ICurrentUserService currentUser,
        IOptions<GovernanceSettings> settings,
        IOptions<JwtSettings> jwt)
    {
        _db = db;
        _clock = clock;
        _separationOfDuties = separationOfDuties;
        _email = email;
        _refreshTokens = refreshTokens;
        _currentUser = currentUser;
        _settings = settings.Value;
        _origin = jwt.Value.Issuer.TrimEnd('/');
    }

    public async Task<ApplicationGovernanceDto?> GetApplicationAsync(Guid applicationSystemId, CancellationToken ct = default)
    {
        var application = await _db.ApplicationSystems.AsNoTracking()
            .Where(item => item.Id == applicationSystemId)
            .Select(item => new { item.Id, item.Code, item.Name })
            .SingleOrDefaultAsync(ct);
        if (application is null)
            return null;
        var settings = await _db.ApplicationGovernance.AsNoTracking().SingleOrDefaultAsync(item => item.ApplicationSystemId == applicationSystemId, ct);
        var owners = await _db.ApplicationOwners.AsNoTracking()
            .Where(owner => owner.ApplicationSystemId == applicationSystemId)
            .OrderBy(owner => owner.User.FullName)
            .Select(owner => new GovernanceUserDto { Id = owner.UserId, FullName = owner.User.FullName, Email = owner.User.Email ?? string.Empty, IsActive = owner.User.IsActive })
            .ToListAsync(ct);
        return new ApplicationGovernanceDto
        {
            ApplicationSystemId = application.Id,
            ApplicationCode = application.Code,
            ApplicationName = application.Name,
            AccessRequestsEnabled = settings?.AccessRequestsEnabled ?? false,
            Owners = owners,
            Version = settings?.Version ?? 0
        };
    }

    public async Task<OperationResult<ApplicationGovernanceDto>> UpdateApplicationAsync(Guid applicationSystemId, UpdateApplicationGovernanceRequest request, CancellationToken ct = default)
    {
        var application = await _db.ApplicationSystems.AsNoTracking().SingleOrDefaultAsync(item => item.Id == applicationSystemId, ct);
        if (application is null)
            return OperationResult<ApplicationGovernanceDto>.Failure("APP_NOT_FOUND", "Application not found.");
        var ownerIds = request.OwnerUserIds.Distinct().ToList();
        if (ownerIds.Count > MaxOwners)
            return OperationResult<ApplicationGovernanceDto>.Failure("GOVERNANCE_INVALID", $"An application has at most {MaxOwners} owners.");
        var owners = await _db.Users.Where(user => ownerIds.Contains(user.Id)).Select(user => new { user.Id, user.IsActive }).ToListAsync(ct);
        if (owners.Count != ownerIds.Count)
            return OperationResult<ApplicationGovernanceDto>.Failure("USER_NOT_FOUND", "One or more owners were not found.");
        if (owners.Any(owner => !owner.IsActive))
            return OperationResult<ApplicationGovernanceDto>.Failure("GOVERNANCE_INVALID", "Owners must be active users.");

        var now = _clock.UtcNow;
        var settings = await _db.ApplicationGovernance.SingleOrDefaultAsync(item => item.ApplicationSystemId == applicationSystemId, ct);
        if (settings is null)
        {
            // Never saved: version 0 on the client. Someone else saving first is a conflict too.
            if (request.Version is > 0)
                return Conflict<ApplicationGovernanceDto>();
            settings = new ApplicationGovernance { ApplicationSystemId = applicationSystemId, CreatedAt = now, Version = 1 };
            _db.ApplicationGovernance.Add(settings);
        }
        else if (!settings.TryAdvance(request.Version))
        {
            return Conflict<ApplicationGovernanceDto>();
        }
        settings.AccessRequestsEnabled = request.AccessRequestsEnabled;
        settings.UpdatedAt = now;

        var current = await _db.ApplicationOwners.Where(owner => owner.ApplicationSystemId == applicationSystemId).ToListAsync(ct);
        var removed = current.Where(owner => !ownerIds.Contains(owner.UserId)).ToList();
        var added = ownerIds.Where(id => current.All(owner => owner.UserId != id)).ToList();
        _db.ApplicationOwners.RemoveRange(removed);
        _db.ApplicationOwners.AddRange(added.Select(id => new ApplicationOwner { Id = Guid.NewGuid(), ApplicationSystemId = applicationSystemId, UserId = id, CreatedAt = now }));
        AddAudit("APPLICATION_GOVERNANCE_UPDATED", nameof(ApplicationSystem), applicationSystemId, application.Code, new
        {
            request.AccessRequestsEnabled,
            ownerCount = ownerIds.Count,
            addedOwners = added,
            removedOwners = removed.Select(owner => owner.UserId)
        });
        await _db.SaveChangesAsync(ct);
        return OperationResult<ApplicationGovernanceDto>.Success((await GetApplicationAsync(applicationSystemId, ct))!);
    }

    /// <summary>Active owners of the application other than the given user, to notify.</summary>
    private async Task<List<(string Email, string Name)>> OwnersToNotifyAsync(Guid applicationSystemId, Guid exceptUserId, CancellationToken ct) =>
        (await _db.ApplicationOwners.AsNoTracking()
            .Where(owner => owner.ApplicationSystemId == applicationSystemId && owner.UserId != exceptUserId && owner.User.IsActive && owner.User.Email != null)
            .Select(owner => new { owner.User.Email, owner.User.FullName })
            .ToListAsync(ct))
        .Select(owner => (owner.Email!, owner.FullName))
        .ToList();

    private Task<bool> IsOwnerAsync(Guid userId, Guid applicationSystemId, CancellationToken ct) =>
        _db.ApplicationOwners.AnyAsync(owner => owner.UserId == userId && owner.ApplicationSystemId == applicationSystemId, ct);

    private string PortalUrl(string section) => $"{_origin}/portal#{section}";

    private void AddAudit(string action, string entityName, Guid entityId, string? applicationCode, object metadata, Guid? actorUserId = null) =>
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = actorUserId ?? _currentUser.UserId,
            ApplicationCode = applicationCode,
            Action = action,
            EntityName = entityName,
            EntityId = entityId.ToString(),
            MetadataJson = JsonSerializer.Serialize(metadata),
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToHexString(),
            CreatedAt = _clock.UtcNow
        });

    private static OperationResult<T> Conflict<T>() =>
        OperationResult<T>.Failure(VersionedUpdates.ConflictCode, "The record changed after it was loaded. Reload it and try again.");

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
