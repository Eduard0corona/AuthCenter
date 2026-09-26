using System.Text.Json;
using System.Diagnostics;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Audit;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Audit;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuthCenter.Infrastructure.Services;

public class AuditService : IAuditService
{
    private readonly IDbContextFactory<AuthCenterDbContext> _dbFactory;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<AuditService> _logger;
    private readonly ICurrentUserService _currentUser;

    public AuditService(
        IDbContextFactory<AuthCenterDbContext> dbFactory,
        IDateTimeProvider dateTimeProvider,
        ILogger<AuditService> logger,
        ICurrentUserService currentUser)
    {
        _dbFactory = dbFactory;
        _dateTimeProvider = dateTimeProvider;
        _logger = logger;
        _currentUser = currentUser;
    }

    public async Task LogAsync(
        string action,
        Guid? userId = null,
        string? applicationCode = null,
        string? entityName = null,
        string? entityId = null,
        string? ipAddress = null,
        string? userAgent = null,
        object? metadata = null,
        CancellationToken ct = default)
    {
        try
        {
            userId ??= _currentUser.UserId;
            var log = new AuditLog
            {
                Id = Guid.NewGuid(),
                Action = action,
                UserId = userId,
                ApplicationCode = applicationCode,
                EntityName = entityName,
                EntityId = entityId,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                MetadataJson = metadata is not null ? JsonSerializer.Serialize(metadata) : null,
                TraceId = Activity.Current?.TraceId.ToHexString(),
                CreatedAt = _dateTimeProvider.UtcNow
            };

            // Use a dedicated context so SaveChangesAsync only flushes the audit entry, not any
            // pending changes in the caller's unit of work. Saving it also queues the deliveries of
            // the event hooks subscribed to this action (AuthCenterDbContext.SaveChangesAsync).
            await using var auditDb = await _dbFactory.CreateDbContextAsync(ct);
            auditDb.AuditLogs.Add(log);
            await auditDb.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist audit log for action {Action}", action);
            throw;
        }
    }

    public async Task<PagedResult<AuditLogDto>> GetAsync(AuditLogQuery query, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var logs = db.AuditLogs.AsNoTracking();

        if (query.UserId.HasValue)
            logs = logs.Where(a => a.UserId == query.UserId.Value);

        if (!string.IsNullOrWhiteSpace(query.ApplicationCode))
            logs = logs.Where(a => a.ApplicationCode == query.ApplicationCode);

        if (!string.IsNullOrWhiteSpace(query.Action))
            logs = logs.Where(a => a.Action == query.Action);

        if (!string.IsNullOrWhiteSpace(query.TraceId))
            logs = logs.Where(a => a.TraceId == query.TraceId);

        if (query.FromUtc.HasValue)
            logs = logs.Where(a => a.CreatedAt >= query.FromUtc.Value);

        if (query.ToUtc.HasValue)
            logs = logs.Where(a => a.CreatedAt <= query.ToUtc.Value);

        if (!string.IsNullOrWhiteSpace(query.EntityName))
            logs = logs.Where(a => a.EntityName == query.EntityName.Trim());

        if (!string.IsNullOrWhiteSpace(query.EntityId))
            logs = logs.Where(a => a.EntityId == query.EntityId.Trim());

        logs = logs.OrderByDescending(a => a.CreatedAt);

        var totalCount = await logs.CountAsync(ct);
        var page = logs.Skip(query.Skip).Take(query.PageSize);
        var items = await (
            from a in page
            join user in db.Users on a.UserId equals user.Id into actors
            from actor in actors.DefaultIfEmpty()
            orderby a.CreatedAt descending
            select new AuditLogDto
            {
                Id = a.Id,
                UserId = a.UserId,
                UserEmail = actor != null ? actor.Email : null,
                UserName = actor != null ? actor.FullName : null,
                ApplicationCode = a.ApplicationCode,
                Action = a.Action,
                EntityName = a.EntityName,
                EntityId = a.EntityId,
                IpAddress = a.IpAddress,
                UserAgent = a.UserAgent,
                MetadataJson = a.MetadataJson,
                TraceId = a.TraceId,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync(ct);

        return PagedResult<AuditLogDto>.Create(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<IReadOnlyList<AuditLogDto>> ExportPageAsync(AuditLogQuery query, CancellationToken ct = default)
    {
        var page = await GetAsync(query, ct);
        await LogAsync("SYSTEM_LOG_EXPORTED", entityName: nameof(AuditLog), metadata: new
        {
            result = "Success",
            query.Page,
            query.PageSize,
            filters = new { query.UserId, query.ApplicationCode, query.Action, query.TraceId, query.FromUtc, query.ToUtc, query.EntityName, query.EntityId }
        }, ct: ct);
        return page.Items;
    }
}
