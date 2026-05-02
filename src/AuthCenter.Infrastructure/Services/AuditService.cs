using System.Text.Json;
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

    public AuditService(
        IDbContextFactory<AuthCenterDbContext> dbFactory,
        IDateTimeProvider dateTimeProvider,
        ILogger<AuditService> logger)
    {
        _dbFactory = dbFactory;
        _dateTimeProvider = dateTimeProvider;
        _logger = logger;
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
                CreatedAt = _dateTimeProvider.UtcNow
            };

            // Use a dedicated context so SaveChangesAsync only flushes the audit entry,
            // not any pending changes in the caller's unit-of-work
            await using var auditDb = await _dbFactory.CreateDbContextAsync(ct);
            auditDb.AuditLogs.Add(log);
            await auditDb.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist audit log for action {Action}", action);
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

        if (query.FromUtc.HasValue)
            logs = logs.Where(a => a.CreatedAt >= query.FromUtc.Value);

        if (query.ToUtc.HasValue)
            logs = logs.Where(a => a.CreatedAt <= query.ToUtc.Value);

        logs = logs.OrderByDescending(a => a.CreatedAt);

        var totalCount = await logs.CountAsync(ct);
        var items = await logs
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(a => new AuditLogDto
            {
                Id = a.Id,
                UserId = a.UserId,
                ApplicationCode = a.ApplicationCode,
                Action = a.Action,
                EntityName = a.EntityName,
                EntityId = a.EntityId,
                IpAddress = a.IpAddress,
                UserAgent = a.UserAgent,
                MetadataJson = a.MetadataJson,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync(ct);

        return PagedResult<AuditLogDto>.Create(items, totalCount, query.Page, query.PageSize);
    }
}
