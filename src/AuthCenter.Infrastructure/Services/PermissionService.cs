using AuthCenter.Application.Common;
using AuthCenter.Application.Common.Exceptions;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Requests.Permissions;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Permissions;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public class PermissionService : IPermissionService
{
    private readonly AuthCenterDbContext _db;
    private readonly IDateTimeProvider _dateTimeProvider;

    public PermissionService(AuthCenterDbContext db, IDateTimeProvider dateTimeProvider)
    {
        _db = db;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<PagedResult<PermissionDto>> GetAllAsync(PaginationQuery pagination, CancellationToken ct = default)
    {
        var query = _db.Permissions.AsNoTracking().OrderBy(p => p.Code);
        var totalCount = await query.CountAsync(ct);
        var perms = await query.Skip(pagination.Skip).Take(pagination.PageSize).ToListAsync(ct);
        return PagedResult<PermissionDto>.Create(perms.Select(MapToDto).ToList(), totalCount, pagination.Page, pagination.PageSize);
    }

    public async Task<PermissionDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var permission = await _db.Permissions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return permission is null ? null : MapToDto(permission);
    }

    public async Task<PagedResult<PermissionDto>> GetByApplicationAsync(Guid applicationSystemId, PaginationQuery pagination, CancellationToken ct = default)
    {
        var query = _db.Permissions
            .Where(p => p.ApplicationSystemId == applicationSystemId)
            .AsNoTracking()
            .OrderBy(p => p.Code);

        var totalCount = await query.CountAsync(ct);
        var perms = await query.Skip(pagination.Skip).Take(pagination.PageSize).ToListAsync(ct);
        return PagedResult<PermissionDto>.Create(perms.Select(MapToDto).ToList(), totalCount, pagination.Page, pagination.PageSize);
    }

    public async Task<OperationResult<PermissionDto>> CreateAsync(CreatePermissionRequest request, CancellationToken ct = default)
    {
        if (!await _db.ApplicationSystems.AnyAsync(a => a.Id == request.ApplicationSystemId, ct))
            throw new NotFoundException(nameof(ApplicationSystem), request.ApplicationSystemId);

        if (await _db.Permissions.AnyAsync(p => p.ApplicationSystemId == request.ApplicationSystemId && p.Code == request.Code, ct))
            return OperationResult<PermissionDto>.Failure("CODE_TAKEN", $"Permission code '{request.Code}' already exists for this application.");

        var permission = new Permission
        {
            Id = Guid.NewGuid(),
            ApplicationSystemId = request.ApplicationSystemId,
            Code = request.Code,
            Name = request.Name,
            Description = request.Description,
            IsActive = true,
            CreatedAt = _dateTimeProvider.UtcNow
        };

        _db.Permissions.Add(permission);
        await _db.SaveChangesAsync(ct);
        return OperationResult<PermissionDto>.Success(MapToDto(permission));
    }

    public async Task<OperationResult<PermissionDto>> UpdateAsync(Guid id, UpdatePermissionRequest request, CancellationToken ct = default)
    {
        var permission = await _db.Permissions.FindAsync([id], ct)
            ?? throw new NotFoundException(nameof(Permission), id);

        permission.Name = request.Name;
        permission.Description = request.Description;

        await _db.SaveChangesAsync(ct);
        return OperationResult<PermissionDto>.Success(MapToDto(permission));
    }

    public async Task<OperationResult> ActivateAsync(Guid id, CancellationToken ct = default)
    {
        var permission = await _db.Permissions.FindAsync([id], ct)
            ?? throw new NotFoundException(nameof(Permission), id);

        permission.IsActive = true;
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeactivateAsync(Guid id, CancellationToken ct = default)
    {
        var permission = await _db.Permissions.FindAsync([id], ct)
            ?? throw new NotFoundException(nameof(Permission), id);

        permission.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    private static PermissionDto MapToDto(Permission p) => new()
    {
        Id = p.Id,
        ApplicationSystemId = p.ApplicationSystemId,
        Code = p.Code,
        Name = p.Name,
        Description = p.Description,
        IsActive = p.IsActive,
        CreatedAt = p.CreatedAt
    };
}
