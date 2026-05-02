using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuthCenter.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IAuditService _auditService;
    private readonly IApplicationService _applicationService;
    private readonly IUserAccessService _userAccessService;
    private readonly IRoleService _roleService;
    private readonly IGoogleAuthService _googleAuthService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IEmailService _emailService;
    private readonly AuthCenterDbContext _db;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        ITokenService tokenService,
        IRefreshTokenService refreshTokenService,
        IAuditService auditService,
        IApplicationService applicationService,
        IUserAccessService userAccessService,
        IRoleService roleService,
        IGoogleAuthService googleAuthService,
        IDateTimeProvider dateTimeProvider,
        IEmailService emailService,
        AuthCenterDbContext db,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _tokenService = tokenService;
        _refreshTokenService = refreshTokenService;
        _auditService = auditService;
        _applicationService = applicationService;
        _userAccessService = userAccessService;
        _roleService = roleService;
        _googleAuthService = googleAuthService;
        _dateTimeProvider = dateTimeProvider;
        _emailService = emailService;
        _db = db;
        _logger = logger;
    }

    public async Task<OperationResult<AuthResponse>> RegisterAsync(RegisterRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var appSystem = await _applicationService.GetByCodeWithSettingsAsync(request.ApplicationCode, ct);
        if (appSystem is null)
            return OperationResult<AuthResponse>.Failure("APP_NOT_FOUND", "Application not found.");

        if (!appSystem.IsActive)
            return OperationResult<AuthResponse>.Failure("APP_INACTIVE", "Application is inactive.");

        var settings = appSystem.RegistrationSettings;
        if (settings is null)
            return OperationResult<AuthResponse>.Failure("APP_NO_SETTINGS", "Application registration settings are missing.");

        if (!settings.AllowPasswordLogin)
            return OperationResult<AuthResponse>.Failure("PASSWORD_LOGIN_DISABLED", "Password login is not allowed for this application.");

        if (settings.RegistrationMode is ApplicationRegistrationMode.Closed or ApplicationRegistrationMode.InviteOnly)
            return OperationResult<AuthResponse>.Failure("REGISTRATION_CLOSED", "Self-registration is not allowed for this application.");

        var existing = await _userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
            return OperationResult<AuthResponse>.Failure("EMAIL_TAKEN", "An account with this email already exists.");

        var now = _dateTimeProvider.UtcNow;
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = request.FullName,
            Email = request.Email,
            UserName = request.Email,
            IsExternalUser = false,
            HasLocalPassword = true,
            IsActive = true,
            CreatedAt = now
        };

        var createResult = await _userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            var errors = createResult.Errors.Select(e => e.Description).ToList();
            return OperationResult<AuthResponse>.Failure("USER_CREATION_FAILED", string.Join("; ", errors), errors);
        }

        bool accessIsActive = settings.RegistrationMode == ApplicationRegistrationMode.Open;
        await _userAccessService.GrantAccessAsync(user.Id, appSystem.Id, accessIsActive, ct);

        if (settings.DefaultRoleId.HasValue)
        {
            var role = await _db.Roles.FindAsync([settings.DefaultRoleId.Value], ct);
            if (role?.Name is not null)
                await _userManager.AddToRoleAsync(user, role.Name);
        }

        await _auditService.LogAsync("USER_REGISTERED", user.Id, appSystem.Code, nameof(ApplicationUser), user.Id.ToString(), ipAddress, userAgent, ct: ct);

        if (settings.RegistrationMode == ApplicationRegistrationMode.ApprovalRequired)
            return OperationResult<AuthResponse>.Failure("APPROVAL_REQUIRED", "Your registration is pending approval.");

        return await BuildAuthResponseAsync(user, appSystem.Code, ipAddress, userAgent, ct);
    }

    public async Task<OperationResult<AuthResponse>> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);

        if (user is null || !user.IsActive)
        {
            await _auditService.LogAsync("LOGIN_FAILED", null, request.ApplicationCode, null, null, ipAddress, userAgent, new { reason = "UserNotFound" }, ct);
            return OperationResult<AuthResponse>.Failure("INVALID_CREDENTIALS", "Invalid email or password.");
        }

        var passwordValid = await _userManager.CheckPasswordAsync(user, request.Password);
        if (!passwordValid)
        {
            await _userManager.AccessFailedAsync(user);
            await _auditService.LogAsync("LOGIN_FAILED", user.Id, request.ApplicationCode, null, null, ipAddress, userAgent, new { reason = "InvalidPassword" }, ct);
            return OperationResult<AuthResponse>.Failure("INVALID_CREDENTIALS", "Invalid email or password.");
        }

        var appSystem = await _applicationService.GetByCodeAsync(request.ApplicationCode, ct);
        if (appSystem is null || !appSystem.IsActive)
        {
            await _auditService.LogAsync("LOGIN_FAILED", user.Id, request.ApplicationCode, null, null, ipAddress, userAgent, new { reason = "AppNotFound" }, ct);
            return OperationResult<AuthResponse>.Failure("APP_NOT_FOUND", "Application not found or inactive.");
        }

        var hasAccess = await _userAccessService.HasActiveAccessAsync(user.Id, appSystem.Id, ct);
        if (!hasAccess)
        {
            await _auditService.LogAsync("LOGIN_FAILED", user.Id, request.ApplicationCode, null, null, ipAddress, userAgent, new { reason = "NoAccess" }, ct);
            return OperationResult<AuthResponse>.Failure("ACCESS_DENIED", "You do not have access to this application.");
        }

        await _userManager.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = _dateTimeProvider.UtcNow;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);

        await _auditService.LogAsync("LOGIN_SUCCESS", user.Id, request.ApplicationCode, null, null, ipAddress, userAgent, ct: ct);
        return await BuildAuthResponseAsync(user, appSystem.Code, ipAddress, userAgent, ct);
    }

    public async Task<OperationResult<AuthResponse>> GoogleLoginAsync(GoogleLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var payload = await _googleAuthService.ValidateIdTokenAsync(request.IdToken, ct);
        if (payload is null)
            return OperationResult<AuthResponse>.Failure("INVALID_GOOGLE_TOKEN", "Google ID token is invalid or expired.");

        var appSystem = await _applicationService.GetByCodeWithSettingsAsync(request.ApplicationCode, ct);
        if (appSystem is null || !appSystem.IsActive)
            return OperationResult<AuthResponse>.Failure("APP_NOT_FOUND", "Application not found or inactive.");

        var settings = appSystem.RegistrationSettings;
        if (settings is null || !settings.AllowGoogleLogin)
            return OperationResult<AuthResponse>.Failure("GOOGLE_LOGIN_DISABLED", "Google login is not allowed for this application.");

        var externalProvider = await _db.ExternalIdentityProviders
            .Include(e => e.User)
            .FirstOrDefaultAsync(e => e.Provider == DomainConstants.Providers.Google && e.ProviderUserId == payload.Subject, ct);

        ApplicationUser? user = externalProvider?.User;

        if (user is null)
        {
            user = await _userManager.FindByEmailAsync(payload.Email);

            if (user is not null)
            {
                _db.ExternalIdentityProviders.Add(new ExternalIdentityProvider
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    Provider = DomainConstants.Providers.Google,
                    ProviderUserId = payload.Subject,
                    Email = payload.Email,
                    DisplayName = payload.Name,
                    PictureUrl = payload.PictureUrl,
                    LinkedAt = _dateTimeProvider.UtcNow,
                    LastUsedAt = _dateTimeProvider.UtcNow,
                    IsActive = true
                });
                await _db.SaveChangesAsync(ct);
            }
            else
            {
                if (settings.RegistrationMode is ApplicationRegistrationMode.Closed or ApplicationRegistrationMode.InviteOnly)
                    return OperationResult<AuthResponse>.Failure("REGISTRATION_CLOSED", "Self-registration is not allowed for this application.");

                var now = _dateTimeProvider.UtcNow;
                user = new ApplicationUser
                {
                    Id = Guid.NewGuid(),
                    FullName = payload.Name ?? payload.Email,
                    Email = payload.Email,
                    UserName = payload.Email,
                    PictureUrl = payload.PictureUrl,
                    IsExternalUser = true,
                    HasLocalPassword = false,
                    IsActive = true,
                    CreatedAt = now,
                    EmailConfirmed = true
                };

                var createResult = await _userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                {
                    var errors = createResult.Errors.Select(e => e.Description).ToList();
                    return OperationResult<AuthResponse>.Failure("USER_CREATION_FAILED", string.Join("; ", errors));
                }

                _db.ExternalIdentityProviders.Add(new ExternalIdentityProvider
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    Provider = DomainConstants.Providers.Google,
                    ProviderUserId = payload.Subject,
                    Email = payload.Email,
                    DisplayName = payload.Name,
                    PictureUrl = payload.PictureUrl,
                    LinkedAt = now,
                    LastUsedAt = now,
                    IsActive = true
                });

                bool accessIsActive = settings.RegistrationMode == ApplicationRegistrationMode.Open;
                await _userAccessService.GrantAccessAsync(user.Id, appSystem.Id, accessIsActive, ct);

                if (settings.DefaultRoleId.HasValue)
                {
                    var role = await _db.Roles.FindAsync([settings.DefaultRoleId.Value], ct);
                    if (role?.Name is not null)
                        await _userManager.AddToRoleAsync(user, role.Name);
                }

                await _db.SaveChangesAsync(ct);
                await _auditService.LogAsync("USER_REGISTERED_GOOGLE", user.Id, appSystem.Code, nameof(ApplicationUser), user.Id.ToString(), ipAddress, userAgent, ct: ct);

                if (settings.RegistrationMode == ApplicationRegistrationMode.ApprovalRequired)
                    return OperationResult<AuthResponse>.Failure("APPROVAL_REQUIRED", "Your registration is pending approval.");
            }
        }
        else
        {
            externalProvider!.LastUsedAt = _dateTimeProvider.UtcNow;
            if (payload.PictureUrl is not null)
            {
                externalProvider.PictureUrl = payload.PictureUrl;
                user.PictureUrl = payload.PictureUrl;
            }
            if (payload.Name is not null)
                externalProvider.DisplayName = payload.Name;
            await _db.SaveChangesAsync(ct);
        }

        if (!user.IsActive)
            return OperationResult<AuthResponse>.Failure("USER_INACTIVE", "Your account is inactive.");

        var hasAccess = await _userAccessService.HasActiveAccessAsync(user.Id, appSystem.Id, ct);
        if (!hasAccess)
            return OperationResult<AuthResponse>.Failure("ACCESS_DENIED", "You do not have access to this application.");

        user.LastLoginAt = _dateTimeProvider.UtcNow;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);

        await _auditService.LogAsync("LOGIN_GOOGLE_SUCCESS", user.Id, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
        return await BuildAuthResponseAsync(user, appSystem.Code, ipAddress, userAgent, ct);
    }

    public async Task<OperationResult<AuthResponse>> RefreshTokenAsync(string refreshToken, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var tokenHash = _tokenService.HashToken(refreshToken);
        var storedToken = await _refreshTokenService.FindByHashAsync(tokenHash, ct);

        if (storedToken is null)
        {
            await _auditService.LogAsync("REFRESH_TOKEN_INVALID", null, null, null, null, ipAddress, userAgent, new { reason = "NotFound" }, ct);
            return OperationResult<AuthResponse>.Failure("INVALID_TOKEN", "Refresh token is invalid.");
        }

        if (storedToken.RevokedAt is not null)
        {
            await _refreshTokenService.RevokeAllForUserAsync(storedToken.UserId, ct);
            await _auditService.LogAsync("REFRESH_TOKEN_REUSE_DETECTED", storedToken.UserId, null, null, null, ipAddress, userAgent, ct: ct);
            return OperationResult<AuthResponse>.Failure("TOKEN_REUSE_DETECTED", "Refresh token has already been used.");
        }

        if (DateTime.UtcNow >= storedToken.ExpiresAt)
        {
            await _auditService.LogAsync("REFRESH_TOKEN_EXPIRED", storedToken.UserId, null, null, null, ipAddress, userAgent, ct: ct);
            return OperationResult<AuthResponse>.Failure("TOKEN_EXPIRED", "Refresh token has expired.");
        }

        if (!storedToken.User.IsActive)
            return OperationResult<AuthResponse>.Failure("USER_INACTIVE", "User account is inactive.");

        var (newToken, newHash) = _tokenService.GenerateRefreshToken();
        await _refreshTokenService.RevokeAsync(storedToken, newHash, ct);
        await _refreshTokenService.CreateAsync(storedToken.UserId, newHash, ipAddress, userAgent, ct);

        var user = storedToken.User;
        user.LastLoginAt = _dateTimeProvider.UtcNow;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);

        var apps = await _userAccessService.GetApplicationCodesForUserAsync(user.Id, ct);
        var roles = await _roleService.GetRoleNamesForUserAsync(user.Id, ct);
        var permissions = await _roleService.GetPermissionCodesForUserAsync(user.Id, ct);

        var accessToken = _tokenService.GenerateAccessToken(user, roles, permissions, apps);

        await _auditService.LogAsync("TOKEN_REFRESHED", user.Id, null, null, null, ipAddress, userAgent, ct: ct);

        return OperationResult<AuthResponse>.Success(new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = newToken,
            ExpiresIn = _tokenService.AccessTokenExpiryMinutes * 60,
            User = new AuthenticatedUserDto
            {
                Id = user.Id,
                FullName = user.FullName,
                Email = user.Email!,
                PictureUrl = user.PictureUrl,
                Applications = apps.ToList(),
                Roles = roles.ToList(),
                Permissions = permissions.ToList()
            }
        });
    }

    public async Task<OperationResult> LogoutAsync(Guid userId, string? refreshToken, CancellationToken ct = default)
    {
        if (refreshToken is not null)
        {
            var tokenHash = _tokenService.HashToken(refreshToken);
            var storedToken = await _refreshTokenService.FindByHashAsync(tokenHash, ct);
            if (storedToken?.UserId == userId && storedToken.RevokedAt is null)
                await _refreshTokenService.RevokeAsync(storedToken, null, ct);
        }

        await _auditService.LogAsync("LOGOUT", userId, ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RevokeTokenAsync(string refreshToken, Guid requestingUserId, CancellationToken ct = default)
    {
        var tokenHash = _tokenService.HashToken(refreshToken);
        var storedToken = await _refreshTokenService.FindByHashAsync(tokenHash, ct);

        if (storedToken is null)
            return OperationResult.Failure("INVALID_TOKEN", "Refresh token not found.");

        if (storedToken.UserId != requestingUserId)
            return OperationResult.Failure("FORBIDDEN", "You can only revoke your own tokens.");

        if (storedToken.RevokedAt is not null)
            return OperationResult.Failure("TOKEN_ALREADY_REVOKED", "Token is already revoked.");

        await _refreshTokenService.RevokeAsync(storedToken, null, ct);
        await _auditService.LogAsync("TOKEN_REVOKED", requestingUserId, ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ForgotPasswordAsync(ForgotPasswordRequest request, string? ipAddress, CancellationToken ct = default)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);

        // Always return success — never reveal whether the email exists
        if (user is null || !user.IsActive || user.IsExternalUser)
        {
            await _auditService.LogAsync("FORGOT_PASSWORD_NOOP", null, request.ApplicationCode, null, null, ipAddress, null, new { email = request.Email }, ct);
            return OperationResult.Success();
        }

        if (!user.HasLocalPassword)
        {
            await _auditService.LogAsync("FORGOT_PASSWORD_NOOP", user.Id, request.ApplicationCode, null, null, ipAddress, null, new { reason = "NoLocalPassword" }, ct);
            return OperationResult.Success();
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);

        await _emailService.SendPasswordResetAsync(user.Email!, user.FullName, token, request.CallbackBaseUrl, ct);
        await _auditService.LogAsync("FORGOT_PASSWORD_SENT", user.Id, request.ApplicationCode, null, null, ipAddress, null, ct: ct);

        return OperationResult.Success();
    }

    public async Task<OperationResult> ResetPasswordAsync(ResetPasswordRequest request, string? ipAddress, CancellationToken ct = default)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null || !user.IsActive)
            return OperationResult.Failure("INVALID_TOKEN", "Password reset failed. The link may be expired or invalid.");

        var result = await _userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description).ToList();
            _logger.LogWarning("Password reset failed for {Email}: {Errors}", request.Email, string.Join("; ", errors));
            return OperationResult.Failure("INVALID_TOKEN", "Password reset failed. The link may be expired or invalid.");
        }

        await _userManager.UpdateSecurityStampAsync(user);
        await _refreshTokenService.RevokeAllForUserAsync(user.Id, ct);
        await _auditService.LogAsync("PASSWORD_RESET", user.Id, null, null, null, ipAddress, null, ct: ct);

        return OperationResult.Success();
    }

    private async Task<OperationResult<AuthResponse>> BuildAuthResponseAsync(ApplicationUser user, string appCode, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        var apps = await _userAccessService.GetApplicationCodesForUserAsync(user.Id, ct);
        var roles = await _roleService.GetRoleNamesForUserAsync(user.Id, ct);
        var permissions = await _roleService.GetPermissionCodesForUserAsync(user.Id, ct);

        var accessToken = _tokenService.GenerateAccessToken(user, roles, permissions, apps);
        var (rawRefresh, refreshHash) = _tokenService.GenerateRefreshToken();
        await _refreshTokenService.CreateAsync(user.Id, refreshHash, ipAddress, userAgent, ct);

        return OperationResult<AuthResponse>.Success(new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = rawRefresh,
            ExpiresIn = _tokenService.AccessTokenExpiryMinutes * 60,
            User = new AuthenticatedUserDto
            {
                Id = user.Id,
                FullName = user.FullName,
                Email = user.Email!,
                PictureUrl = user.PictureUrl,
                Applications = apps.ToList(),
                Roles = roles.ToList(),
                Permissions = permissions.ToList()
            }
        });
    }
}
