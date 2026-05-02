using System.Text.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace AuthCenter.Infrastructure.Services;

public class AuditService : IAuditService
{
    private readonly AuthCenterDbContext _db;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<AuditService> _logger;

    public AuditService(AuthCenterDbContext db, IDateTimeProvider dateTimeProvider, ILogger<AuditService> logger)
    {
        _db = db;
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
            _db.AuditLogs.Add(log);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist audit log for action {Action}", action);
        }
    }
}
