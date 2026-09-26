using AuthCenter.Application.Common;
using AuthCenter.Application.Common.Exceptions;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Requests.Applications;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Applications;
using AuthCenter.Domain.Constants;
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
    private readonly IAuditService _audit;
    private static readonly TimeSpan AppCacheTtl = TimeSpan.FromMinutes(5);

    public ApplicationService(AuthCenterDbContext db, IDateTimeProvider dateTimeProvider, IMemoryCache cache, IAuditService audit)
    {
        _db = db;
        _dateTimeProvider = dateTimeProvider;
        _cache = cache;
        _audit = audit;
    }

    public async Task<PagedResult<ApplicationDto>> GetAllAsync(PaginationQuery pagination, CancellationToken ct = default)
    {
        var query = _db.ApplicationSystems
            .Include(a => a.RegistrationSettings)
            .Include(a => a.BrandingSettings)
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
            .Include(a => a.BrandingSettings)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        return app is null ? null : MapToDto(app);
    }

    public Task<ApplicationSystem?> GetByCodeAsync(string code, CancellationToken ct = default) =>
        _db.ApplicationSystems
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Code == code, ct);

    public async Task<LoginOptionsResponse?> GetLoginOptionsAsync(string code, CancellationToken ct = default)
    {
        var application = await _db.ApplicationSystems.AsNoTracking()
            .Where(item => item.Code == code && item.IsActive)
            .Select(item => new
            {
                item.Id,
                item.Code,
                item.Name,
                AllowPasswordLogin = item.RegistrationSettings == null || item.RegistrationSettings.AllowPasswordLogin,
                AllowMagicLink = item.RegistrationSettings != null && item.RegistrationSettings.AllowMagicLink
            })
            .FirstOrDefaultAsync(ct);
        if (application is null)
            return null;
        return new LoginOptionsResponse
        {
            ApplicationCode = application.Code,
            ApplicationName = application.Name,
            AllowPasswordLogin = application.AllowPasswordLogin,
            AllowMagicLink = application.AllowMagicLink,
            FederationAvailable = await _db.FederationProviders.AnyAsync(provider => provider.ApplicationSystemId == application.Id && provider.IsActive, ct)
        };
    }

    public async Task<ApplicationBrandingDto?> GetBrandingAsync(string code, CancellationToken ct = default)
    {
        var app = await _db.ApplicationSystems
            .AsNoTracking()
            .Include(item => item.BrandingSettings)
            .FirstOrDefaultAsync(item => item.Code == code && item.IsActive, ct);
        return app is null ? null : MapBranding(app);
    }

    public async Task<OperationResult<ApplicationBrandingDto>> UpdateBrandingAsync(
        Guid applicationId,
        UpdateApplicationBrandingRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 100)
            return OperationResult<ApplicationBrandingDto>.Failure("INVALID_DISPLAY_NAME", "Display name is required and cannot exceed 100 characters.");
        if (!IsHexColor(request.PrimaryColor) || !IsHexColor(request.BackgroundColor))
            return OperationResult<ApplicationBrandingDto>.Failure("INVALID_COLOR", "Brand colors must use the #RRGGBB format.");
        if (!AreSafeBrandingUrls(request.LogoUrl, request.SupportUrl, request.PrivacyUrl, request.TermsUrl))
            return OperationResult<ApplicationBrandingDto>.Failure("INVALID_BRANDING_URL", "Branding links must be absolute HTTPS URLs without embedded credentials.");

        var app = await _db.ApplicationSystems
            .Include(item => item.BrandingSettings)
            .FirstOrDefaultAsync(item => item.Id == applicationId, ct)
            ?? throw new NotFoundException(nameof(ApplicationSystem), applicationId);
        var now = _dateTimeProvider.UtcNow;
        if (app.BrandingSettings is null)
        {
            app.BrandingSettings = new ApplicationBrandingSettings
            {
                Id = Guid.NewGuid(),
                ApplicationSystemId = app.Id,
                CreatedAt = now
            };
            _db.ApplicationBrandingSettings.Add(app.BrandingSettings);
        }
        app.BrandingSettings.DisplayName = request.DisplayName.Trim();
        app.BrandingSettings.PrimaryColor = request.PrimaryColor.ToUpperInvariant();
        app.BrandingSettings.BackgroundColor = request.BackgroundColor.ToUpperInvariant();
        app.BrandingSettings.LogoUrl = NormalizeUrl(request.LogoUrl);
        app.BrandingSettings.SupportUrl = NormalizeUrl(request.SupportUrl);
        app.BrandingSettings.PrivacyUrl = NormalizeUrl(request.PrivacyUrl);
        app.BrandingSettings.TermsUrl = NormalizeUrl(request.TermsUrl);
        app.BrandingSettings.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("APPLICATION_BRANDING_UPDATED", applicationCode: app.Code, entityName: nameof(ApplicationBrandingSettings), entityId: app.BrandingSettings.Id.ToString(), metadata: new { result = "Success", applicationId, links = new { hasLogo = app.BrandingSettings.LogoUrl is not null, hasSupport = app.BrandingSettings.SupportUrl is not null, hasPrivacy = app.BrandingSettings.PrivacyUrl is not null, hasTerms = app.BrandingSettings.TermsUrl is not null } }, ct: ct);
        return OperationResult<ApplicationBrandingDto>.Success(MapBranding(app));
    }

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

        if (request.DefaultRoleId.HasValue)
            return OperationResult<ApplicationDto>.Failure("DEFAULT_ROLE_REQUIRES_APPLICATION", "Create the application before assigning one of its roles as the default.");

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
                AllowMicrosoftLogin = request.AllowMicrosoftLogin,
                AllowGitHubLogin = request.AllowGitHubLogin,
                AllowAppleLogin = request.AllowAppleLogin,
                AllowMagicLink = request.AllowMagicLink,
                AllowPasswordLogin = request.AllowPasswordLogin,
                RequireEmailConfirmation = request.RequireEmailConfirmation,
                RequireMfa = request.RequireMfa,
                AllowedEmailDomains = request.AllowedEmailDomains,
                DefaultRoleId = request.DefaultRoleId,
                CreatedAt = now
            },
            BrandingSettings = new ApplicationBrandingSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = request.Name.Trim(),
                CreatedAt = now
            }
        };

        _db.ApplicationSystems.Add(app);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("APPLICATION_CREATED", applicationCode: app.Code, entityName: nameof(ApplicationSystem), entityId: app.Id.ToString(), metadata: new { result = "Success", registrationMode = mode.ToString() }, ct: ct);
        return OperationResult<ApplicationDto>.Success(MapToDto(app));
    }

    public async Task<OperationResult<ApplicationDto>> UpdateAsync(Guid id, UpdateApplicationRequest request, CancellationToken ct = default)
    {
        var app = await _db.ApplicationSystems
            .Include(a => a.RegistrationSettings)
            .Include(a => a.BrandingSettings)
            .FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException(nameof(ApplicationSystem), id);

        if (!Enum.TryParse<ApplicationRegistrationMode>(request.RegistrationMode, out var mode))
            return OperationResult<ApplicationDto>.Failure("INVALID_MODE", "Invalid registration mode.");

        if (request.DefaultRoleId.HasValue && !await _db.Roles.AnyAsync(
                role => role.Id == request.DefaultRoleId.Value &&
                    role.ApplicationSystemId == id &&
                    role.IsActive, ct))
            return OperationResult<ApplicationDto>.Failure("DEFAULT_ROLE_INVALID", "Default role must be active and belong to this application.");

        app.Name = request.Name;
        app.Description = request.Description;
        app.UpdatedAt = _dateTimeProvider.UtcNow;

        if (app.RegistrationSettings is not null)
        {
            app.RegistrationSettings.RegistrationMode = mode;
            app.RegistrationSettings.AllowGoogleLogin = request.AllowGoogleLogin;
            app.RegistrationSettings.AllowMicrosoftLogin = request.AllowMicrosoftLogin;
            app.RegistrationSettings.AllowGitHubLogin = request.AllowGitHubLogin;
            app.RegistrationSettings.AllowAppleLogin = request.AllowAppleLogin;
            app.RegistrationSettings.AllowMagicLink = request.AllowMagicLink;
            app.RegistrationSettings.AllowPasswordLogin = request.AllowPasswordLogin;
            app.RegistrationSettings.RequireEmailConfirmation = request.RequireEmailConfirmation;
            app.RegistrationSettings.RequireMfa = request.RequireMfa;
            app.RegistrationSettings.AllowedEmailDomains = request.AllowedEmailDomains;
            app.RegistrationSettings.DefaultRoleId = request.DefaultRoleId;
            app.RegistrationSettings.UpdatedAt = _dateTimeProvider.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        _cache.Remove($"app_settings:{app.Code}");
        await _audit.LogAsync("APPLICATION_UPDATED", applicationCode: app.Code, entityName: nameof(ApplicationSystem), entityId: app.Id.ToString(), metadata: new { result = "Success", registrationMode = mode.ToString(), request.DefaultRoleId }, ct: ct);
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
        await _audit.LogAsync("APPLICATION_ACTIVATED", applicationCode: app.Code, entityName: nameof(ApplicationSystem), entityId: app.Id.ToString(), metadata: new { result = "Success" }, ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeactivateAsync(Guid id, CancellationToken ct = default)
    {
        var app = await _db.ApplicationSystems.FindAsync([id], ct)
            ?? throw new NotFoundException(nameof(ApplicationSystem), id);
        if (string.Equals(app.Code, DomainConstants.SystemCodes.AuthCenter, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Failure(
                "SYSTEM_APPLICATION_REQUIRED",
                "The first-party AuthCenter application cannot be deactivated.");
        }
        var now = _dateTimeProvider.UtcNow;
        if (_db.Database.IsRelational())
        {
            await _db.RefreshTokens
                .Where(token => token.ApplicationCode == app.Code && token.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), ct);
        }
        else
        {
            var activeTokens = await _db.RefreshTokens
                .Where(token => token.ApplicationCode == app.Code && token.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var token in activeTokens)
                token.RevokedAt = now;
        }

        app.IsActive = false;
        app.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        _cache.Remove($"app_settings:{app.Code}");
        await _audit.LogAsync("APPLICATION_DEACTIVATED", applicationCode: app.Code, entityName: nameof(ApplicationSystem), entityId: app.Id.ToString(), metadata: new { result = "Success", sessionsRevoked = true }, ct: ct);
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
            AllowMicrosoftLogin = app.RegistrationSettings.AllowMicrosoftLogin,
            AllowGitHubLogin = app.RegistrationSettings.AllowGitHubLogin,
            AllowAppleLogin = app.RegistrationSettings.AllowAppleLogin,
            AllowMagicLink = app.RegistrationSettings.AllowMagicLink,
            AllowPasswordLogin = app.RegistrationSettings.AllowPasswordLogin,
            RequireEmailConfirmation = app.RegistrationSettings.RequireEmailConfirmation,
            RequireMfa = app.RegistrationSettings.RequireMfa,
            AllowedEmailDomains = app.RegistrationSettings.AllowedEmailDomains,
            DefaultRoleId = app.RegistrationSettings.DefaultRoleId
        },
        Branding = MapBranding(app)
    };

    private static ApplicationBrandingDto MapBranding(ApplicationSystem app)
    {
        var branding = app.BrandingSettings;
        return new ApplicationBrandingDto
        {
            ApplicationCode = app.Code,
            DisplayName = branding?.DisplayName ?? app.Name,
            PrimaryColor = branding?.PrimaryColor ?? "#2563EB",
            BackgroundColor = branding?.BackgroundColor ?? "#F8FAFC",
            LogoUrl = branding?.LogoUrl,
            SupportUrl = branding?.SupportUrl,
            PrivacyUrl = branding?.PrivacyUrl,
            TermsUrl = branding?.TermsUrl
        };
    }

    private static bool IsHexColor(string value) =>
        value.Length == 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit);

    private static bool AreSafeBrandingUrls(params string?[] values) => values.All(value =>
        string.IsNullOrWhiteSpace(value) ||
        (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
         uri.Scheme == Uri.UriSchemeHttps &&
         string.IsNullOrEmpty(uri.UserInfo)));

    private static string? NormalizeUrl(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
