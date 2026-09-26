using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.ApiResources;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.ApiResources;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public sealed class ApiResourceService : IApiResourceService
{
    private readonly AuthCenterDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public ApiResourceService(AuthCenterDbContext db, IDateTimeProvider clock, IAuditService audit)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
    }

    public async Task<PagedResult<ApiResourceResponse>> GetAllAsync(ApiResourceQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var resources = _db.ApiResources.AsNoTracking();
        if (query.ApplicationSystemId.HasValue)
            resources = resources.Where(resource => resource.ApplicationSystemId == query.ApplicationSystemId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            resources = resources.Where(resource => resource.DisplayName.Contains(search) || resource.Identifier.Contains(search));
        }

        var total = await resources.CountAsync(ct);
        var items = await resources
            .Include(resource => resource.ApplicationSystem)
            .Include(resource => resource.Scopes)
            .OrderBy(resource => resource.DisplayName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return PagedResult<ApiResourceResponse>.Create(items.Select(Map).ToList(), total, page, pageSize);
    }

    public async Task<ApiResourceResponse?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var resource = await Load(id).AsNoTracking().FirstOrDefaultAsync(ct);
        return resource is null ? null : Map(resource);
    }

    public async Task<OperationResult<ApiResourceResponse>> CreateAsync(CreateApiResourceRequest request, CancellationToken ct = default)
    {
        var application = await _db.ApplicationSystems.FirstOrDefaultAsync(item => item.Id == request.ApplicationSystemId, ct);
        if (application is null || !application.IsActive)
            return OperationResult<ApiResourceResponse>.Failure("APPLICATION_NOT_FOUND", "Active application not found.");
        if (await _db.ApiResources.AnyAsync(resource => resource.Identifier == request.Identifier, ct))
            return OperationResult<ApiResourceResponse>.Failure("API_IDENTIFIER_TAKEN", "Another API already uses this identifier.");
        var taken = await TakenScopeNamesAsync(request.Scopes.Select(scope => scope.Name), null, ct);
        if (taken.Count > 0)
            return OperationResult<ApiResourceResponse>.Failure("API_SCOPE_TAKEN", $"Scope names are unique across APIs; already used: {string.Join(", ", taken)}.");

        var now = _clock.UtcNow;
        var resource = new ApiResource
        {
            Id = Guid.NewGuid(),
            ApplicationSystemId = application.Id,
            ApplicationSystem = application,
            Identifier = request.Identifier,
            DisplayName = request.DisplayName.Trim(),
            Description = request.Description,
            IsActive = true,
            CreatedAt = now
        };
        foreach (var scope in request.Scopes)
            resource.Scopes.Add(new ApiScope { Id = Guid.NewGuid(), Name = scope.Name, DisplayName = scope.DisplayName.Trim(), Description = scope.Description, CreatedAt = now });
        _db.ApiResources.Add(resource);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("API_RESOURCE_CREATED", applicationCode: application.Code, entityName: nameof(ApiResource), entityId: resource.Id.ToString(),
            metadata: new { resource.Identifier, scopes = request.Scopes.Select(scope => scope.Name) }, ct: ct);
        return OperationResult<ApiResourceResponse>.Success(Map(resource));
    }

    public async Task<OperationResult<ApiResourceResponse>> UpdateAsync(Guid id, UpdateApiResourceRequest request, CancellationToken ct = default)
    {
        var resource = await Load(id).FirstOrDefaultAsync(ct);
        if (resource is null)
            return OperationResult<ApiResourceResponse>.Failure("NOT_FOUND", "API not found.");
        var taken = await TakenScopeNamesAsync(request.Scopes.Select(scope => scope.Name), resource.Id, ct);
        if (taken.Count > 0)
            return OperationResult<ApiResourceResponse>.Failure("API_SCOPE_TAKEN", $"Scope names are unique across APIs; already used: {string.Join(", ", taken)}.");
        // The scopes live in their own rows: the API's version covers them.
        if (!resource.TryAdvance(request.Version))
            return OperationResult<ApiResourceResponse>.Failure(VersionedUpdates.ConflictCode, "The API changed after it was loaded.");

        var now = _clock.UtcNow;
        resource.DisplayName = request.DisplayName.Trim();
        resource.Description = request.Description;
        resource.IsActive = request.IsActive;
        resource.UpdatedAt = now;

        var requested = request.Scopes.ToDictionary(scope => scope.Name, StringComparer.Ordinal);
        foreach (var removed in resource.Scopes.Where(scope => !requested.ContainsKey(scope.Name)).ToList())
            _db.ApiScopes.Remove(removed);
        foreach (var scope in request.Scopes)
        {
            var existing = resource.Scopes.FirstOrDefault(item => item.Name == scope.Name);
            if (existing is null)
            {
                _db.ApiScopes.Add(new ApiScope { Id = Guid.NewGuid(), ApiResourceId = resource.Id, Name = scope.Name, DisplayName = scope.DisplayName.Trim(), Description = scope.Description, CreatedAt = now });
            }
            else
            {
                existing.DisplayName = scope.DisplayName.Trim();
                existing.Description = scope.Description;
            }
        }

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("API_RESOURCE_UPDATED", applicationCode: resource.ApplicationSystem.Code, entityName: nameof(ApiResource), entityId: resource.Id.ToString(),
            metadata: new { resource.Identifier, resource.IsActive, scopes = request.Scopes.Select(scope => scope.Name) }, ct: ct);
        return OperationResult<ApiResourceResponse>.Success(Map((await Load(id).AsNoTracking().FirstAsync(ct))));
    }

    private IQueryable<ApiResource> Load(Guid id) =>
        _db.ApiResources.Include(resource => resource.ApplicationSystem).Include(resource => resource.Scopes).Where(resource => resource.Id == id);

    private async Task<List<string>> TakenScopeNamesAsync(IEnumerable<string> names, Guid? ownResourceId, CancellationToken ct)
    {
        var requested = names.ToList();
        return await _db.ApiScopes.AsNoTracking()
            .Where(scope => requested.Contains(scope.Name) && scope.ApiResourceId != ownResourceId)
            .Select(scope => scope.Name)
            .ToListAsync(ct);
    }

    private static ApiResourceResponse Map(ApiResource resource) => new()
    {
        Version = resource.Version,
        Id = resource.Id,
        ApplicationSystemId = resource.ApplicationSystemId,
        ApplicationCode = resource.ApplicationSystem.Code,
        ApplicationName = resource.ApplicationSystem.Name,
        Identifier = resource.Identifier,
        DisplayName = resource.DisplayName,
        Description = resource.Description,
        IsActive = resource.IsActive,
        Scopes = resource.Scopes.OrderBy(scope => scope.Name).Select(scope => new ApiScopeResponse
        {
            Id = scope.Id,
            Name = scope.Name,
            DisplayName = scope.DisplayName,
            Description = scope.Description
        }).ToList(),
        CreatedAt = resource.CreatedAt,
        UpdatedAt = resource.UpdatedAt
    };
}
