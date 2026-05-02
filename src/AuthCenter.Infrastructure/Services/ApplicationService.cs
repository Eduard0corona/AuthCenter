using AuthCenter.Application.Common;
using AuthCenter.Application.Common.Exceptions;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Applications;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Applications;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AuthCenter.Infrastructure.Services;

public class ApplicationService : IApplicationService
{
    private readonly AuthCenterDbContext _db;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan AppCacheTtl = TimeSpan.FromMinutes(5);

    public ApplicationService(AuthCenterDbContext db, IDateTimeProvider dateTimeProvider, IMemoryCache cache)
    {
        _db = db;
        _dateTimeProvider = dateTimeProvider;
        _cache = cache;
    }

    public async Task<PagedResult<ApplicationDto>> GetAllAsync(PaginationQuery pagination, CancellationToken ct = default)
    {
        var query = _db.ApplicationSystems
            .Include(a => a.RegistrationSettings)
            .AsNoTracking()
            .OrderBy(a => a.Name);

        var totalCount = await query.CountAsync(ct);
        var apps = await query.Skip(pagination.Skip).Take(pagination.PageSize).ToListAsync(ct);

        return PagedResult<ApplicationDto>.Create(apps.Select(MapToDto).ToList(), totalCount, pagination.Page, pagination.PageSize);
    }

    public async Task<ApplicationDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var app = await _db.ApplicationSystems
            .Include(a => a.RegistrationSettings)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        return app is null ? null : MapToDto(app);
    }

    public Task<ApplicationSystem?> GetByCodeAsync(string code, CancellationToken ct = default) =>
        _db.ApplicationSystems
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Code == code, ct);

    public async Task<ApplicationSystem?> GetByCodeWithSettingsAsync(string code, CancellationToken ct = default)
    {
        var cacheKey = $"app_settings:{code}";
        if (_cache.TryGetValue(cacheKey, out ApplicationSystem? cached))
            return cached;

        var app = await _db.ApplicationSystems
            .Include(a => a.RegistrationSettings)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Code == code, ct);

        if (app is not null)
            _cache.Set(cacheKey, app, AppCacheTtl);

        return app;
    }

    public async Task<OperationResult<ApplicationDto>> CreateAsync(CreateApplicationRequest request, CancellationToken ct = default)
    {
        if (await _db.ApplicationSystems.AnyAsync(a => a.Code == request.Code, ct))
            return OperationResult<ApplicationDto>.Failure("CODE_TAKEN", $"Application code '{request.Code}' is already in use.");

        if (!Enum.TryParse<ApplicationRegistrationMode>(request.RegistrationMode, out var mode))
            return OperationResult<ApplicationDto>.Failure("INVALID_MODE", "Invalid registration mode.");

        if (request.DefaultRoleId.HasValue && !await _db.Roles.AnyAsync(r => r.Id == request.DefaultRoleId.Value, ct))
            return OperationResult<ApplicationDto>.Failure("DEFAULT_ROLE_NOT_FOUND", "Default role was not found.");

        var now = _dateTimeProvider.UtcNow;
        var app = new ApplicationSystem
        {
            Id = Guid.NewGuid(),
            Code = request.Code,
            Name = request.Name,
            Description = request.Description,
            IsActive = true,
            CreatedAt = now,
            RegistrationSettings = new ApplicationRegistrationSettings
            {
                Id = Guid.NewGuid(),
                RegistrationMode = mode,
                AllowGoogleLogin = request.AllowGoogleLogin,
                AllowPasswordLogin = request.AllowPasswordLogin,
                RequireEmailConfirmation = request.RequireEmailConfirmation,
                AllowedEmailDomains = request.AllowedEmailDomains,
                DefaultRoleId = request.DefaultRoleId,
                CreatedAt = now
            }
        };

        _db.ApplicationSystems.Add(app);
        await _db.SaveChangesAsync(ct);
        return OperationResult<ApplicationDto>.Success(MapToDto(app));
    }

    public async Task<OperationResult<ApplicationDto>> UpdateAsync(Guid id, UpdateApplicationRequest request, CancellationToken ct = default)
    {
        var app = await _db.ApplicationSystems
            .Include(a => a.RegistrationSettings)
            .FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException(nameof(ApplicationSystem), id);

        if (!Enum.TryParse<ApplicationRegistrationMode>(request.RegistrationMode, out var mode))
            return OperationResult<ApplicationDto>.Failure("INVALID_MODE", "Invalid registration mode.");

        if (request.DefaultRoleId.HasValue && !await _db.Roles.AnyAsync(r => r.Id == request.DefaultRoleId.Value, ct))
            return OperationResult<ApplicationDto>.Failure("DEFAULT_ROLE_NOT_FOUND", "Default role was not found.");

        app.Name = request.Name;
        app.Description = request.Description;
        app.UpdatedAt = _dateTimeProvider.UtcNow;

        if (app.RegistrationSettings is not null)
        {
            app.RegistrationSettings.RegistrationMode = mode;
            app.RegistrationSettings.AllowGoogleLogin = request.AllowGoogleLogin;
            app.RegistrationSettings.AllowPasswordLogin = request.AllowPasswordLogin;
            app.RegistrationSettings.RequireEmailConfirmation = request.RequireEmailConfirmation;
            app.RegistrationSettings.AllowedEmailDomains = request.AllowedEmailDomains;
            app.RegistrationSettings.DefaultRoleId = request.DefaultRoleId;
            app.RegistrationSettings.UpdatedAt = _dateTimeProvider.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        _cache.Remove($"app_settings:{app.Code}");
        return OperationResult<ApplicationDto>.Success(MapToDto(app));
    }

    public async Task<OperationResult> ActivateAsync(Guid id, CancellationToken ct = default)
    {
        var app = await _db.ApplicationSystems.FindAsync([id], ct)
            ?? throw new NotFoundException(nameof(ApplicationSystem), id);
        app.IsActive = true;
        app.UpdatedAt = _dateTimeProvider.UtcNow;
        await _db.SaveChangesAsync(ct);
        _cache.Remove($"app_settings:{app.Code}");
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeactivateAsync(Guid id, CancellationToken ct = default)
    {
        var app = await _db.ApplicationSystems.FindAsync([id], ct)
            ?? throw new NotFoundException(nameof(ApplicationSystem), id);
        app.IsActive = false;
        app.UpdatedAt = _dateTimeProvider.UtcNow;
        await _db.SaveChangesAsync(ct);
        _cache.Remove($"app_settings:{app.Code}");
        return OperationResult.Success();
    }

    private static ApplicationDto MapToDto(ApplicationSystem app) => new()
    {
        Id = app.Id,
        Code = app.Code,
        Name = app.Name,
        Description = app.Description,
        IsActive = app.IsActive,
        CreatedAt = app.CreatedAt,
        UpdatedAt = app.UpdatedAt,
        RegistrationSettings = app.RegistrationSettings is null ? null : new ApplicationRegistrationSettingsDto
        {
            RegistrationMode = app.RegistrationSettings.RegistrationMode.ToString(),
            AllowGoogleLogin = app.RegistrationSettings.AllowGoogleLogin,
            AllowPasswordLogin = app.RegistrationSettings.AllowPasswordLogin,
            RequireEmailConfirmation = app.RegistrationSettings.RequireEmailConfirmation,
            AllowedEmailDomains = app.RegistrationSettings.AllowedEmailDomains,
            DefaultRoleId = app.RegistrationSettings.DefaultRoleId
        }
    };
}
