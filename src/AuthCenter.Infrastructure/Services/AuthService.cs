using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Identity;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly IMicrosoftAuthService _microsoftAuthService;
    private readonly IGitHubAuthService _gitHubAuthService;
    private readonly IAppleAuthService _appleAuthService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IEmailService _emailService;
    private readonly IMfaService _mfaService;
    private readonly IMemoryCache _memoryCache;
    private readonly MfaSettings _mfaSettings;
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
        IMicrosoftAuthService microsoftAuthService,
        IGitHubAuthService gitHubAuthService,
        IAppleAuthService appleAuthService,
        IDateTimeProvider dateTimeProvider,
        IEmailService emailService,
        IMfaService mfaService,
        IMemoryCache memoryCache,
        IOptions<MfaSettings> mfaSettings,
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
        _microsoftAuthService = microsoftAuthService;
        _gitHubAuthService = gitHubAuthService;
        _appleAuthService = appleAuthService;
        _dateTimeProvider = dateTimeProvider;
        _emailService = emailService;
        _mfaService = mfaService;
        _memoryCache = memoryCache;
        _mfaSettings = mfaSettings.Value;
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

        if (!IsEmailDomainAllowed(request.Email, settings.AllowedEmailDomains))
            return OperationResult<AuthResponse>.Failure("EMAIL_DOMAIN_NOT_ALLOWED", "Email domain is not allowed for this application.");

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
            EmailConfirmed = !settings.RequireEmailConfirmation,
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

        if (settings.RequireEmailConfirmation)
        {
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            await _emailService.SendEmailConfirmationAsync(user.Email!, user.FullName, token, null, ct);
            return OperationResult<AuthResponse>.Failure("EMAIL_CONFIRMATION_REQUIRED", "Please confirm your email before signing in.");
        }

        if (settings.RegistrationMode == ApplicationRegistrationMode.ApprovalRequired)
            return OperationResult<AuthResponse>.Failure("APPROVAL_REQUIRED", "Your registration is pending approval.");

        return await BuildAuthResponseAsync(user, appSystem.Id, appSystem.Code, ipAddress, userAgent, ct);
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

        var appSystem = await _applicationService.GetByCodeWithSettingsAsync(request.ApplicationCode, ct);
        if (appSystem is null || !appSystem.IsActive)
        {
            await _auditService.LogAsync("LOGIN_FAILED", user.Id, request.ApplicationCode, null, null, ipAddress, userAgent, new { reason = "AppNotFound" }, ct);
            return OperationResult<AuthResponse>.Failure("APP_NOT_FOUND", "Application not found or inactive.");
        }

        if (appSystem.RegistrationSettings?.RequireEmailConfirmation == true && !user.EmailConfirmed)
        {
            await _auditService.LogAsync("LOGIN_FAILED", user.Id, request.ApplicationCode, null, null, ipAddress, userAgent, new { reason = "EmailNotConfirmed" }, ct);
            return OperationResult<AuthResponse>.Failure("EMAIL_NOT_CONFIRMED", "Please confirm your email before signing in.");
        }

        var hasAccess = await _userAccessService.HasActiveAccessAsync(user.Id, appSystem.Id, ct);
        if (!hasAccess)
        {
            await _auditService.LogAsync("LOGIN_FAILED", user.Id, request.ApplicationCode, null, null, ipAddress, userAgent, new { reason = "NoAccess" }, ct);
            return OperationResult<AuthResponse>.Failure("ACCESS_DENIED", "You do not have access to this application.");
        }

        if (user.MustChangePassword)
        {
            var forcedToken = _tokenService.GenerateForcedChangePendingToken(user.Id, appSystem.Code);
            await _auditService.LogAsync("PASSWORD_CHANGE_REQUIRED", user.Id, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
            return OperationResult<AuthResponse>.Failure("PASSWORD_CHANGE_REQUIRED", forcedToken);
        }

        var mfaResult = await RequireMfaIfNeededAsync(user.Id, appSystem, request.DeviceToken, ipAddress, userAgent, ct);
        if (mfaResult is not null)
            return mfaResult;

        await _userManager.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = _dateTimeProvider.UtcNow;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);

        await _auditService.LogAsync("LOGIN_SUCCESS", user.Id, request.ApplicationCode, null, null, ipAddress, userAgent, ct: ct);
        return await BuildAuthResponseAsync(user, appSystem.Id, appSystem.Code, ipAddress, userAgent, ct);
    }

    public async Task<OperationResult<AuthResponse>> GoogleLoginAsync(GoogleLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var payload = await _googleAuthService.ValidateIdTokenAsync(request.IdToken, ct);
        if (payload is null)
            return OperationResult<AuthResponse>.Failure("INVALID_GOOGLE_TOKEN", "Google ID token is invalid or expired.");

        var externalPayload = new ExternalTokenPayload
        {
            Subject = payload.Subject,
            Email = payload.Email,
            Name = payload.Name,
            PictureUrl = payload.PictureUrl
        };

        return await ExternalLoginAsync(
            externalPayload,
            request.ApplicationCode,
            request.DeviceToken,
            DomainConstants.Providers.Google,
            settings => settings.AllowGoogleLogin,
            "GOOGLE_LOGIN_DISABLED",
            "Google login is not allowed for this application.",
            "USER_REGISTERED_GOOGLE",
            "LOGIN_GOOGLE_SUCCESS",
            ipAddress,
            userAgent,
            ct);
    }

    public async Task<OperationResult<AuthResponse>> MicrosoftLoginAsync(MicrosoftLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var payload = await _microsoftAuthService.ValidateIdTokenAsync(request.IdToken, ct);
        if (payload is null)
            return OperationResult<AuthResponse>.Failure("INVALID_MICROSOFT_TOKEN", "Microsoft ID token is invalid or expired.");

        return await ExternalLoginAsync(
            payload,
            request.ApplicationCode,
            request.DeviceToken,
            DomainConstants.Providers.Microsoft,
            settings => settings.AllowMicrosoftLogin,
            "MICROSOFT_LOGIN_DISABLED",
            "Microsoft login is not allowed for this application.",
            "USER_REGISTERED_MICROSOFT",
            "LOGIN_MICROSOFT_SUCCESS",
            ipAddress,
            userAgent,
            ct);
    }

    public async Task<OperationResult<AuthResponse>> GitHubLoginAsync(GitHubLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var payload = await _gitHubAuthService.GetUserFromAccessTokenAsync(request.AccessToken, ct);
        if (payload is null)
            return OperationResult<AuthResponse>.Failure("INVALID_GITHUB_TOKEN", "GitHub access token is invalid.");

        if (string.IsNullOrWhiteSpace(payload.Email))
            return OperationResult<AuthResponse>.Failure("GITHUB_EMAIL_NOT_AVAILABLE", "GitHub account does not expose a verified primary email.");

        return await ExternalLoginAsync(
            payload,
            request.ApplicationCode,
            request.DeviceToken,
            DomainConstants.Providers.GitHub,
            settings => settings.AllowGitHubLogin,
            "GITHUB_LOGIN_DISABLED",
            "GitHub login is not allowed for this application.",
            "USER_REGISTERED_GITHUB",
            "LOGIN_GITHUB_SUCCESS",
            ipAddress,
            userAgent,
            ct);
    }

    public async Task<OperationResult<AuthResponse>> AppleLoginAsync(AppleLoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var payload = await _appleAuthService.ValidateIdTokenAsync(request.IdToken, ct);
        if (payload is null)
            return OperationResult<AuthResponse>.Failure("INVALID_APPLE_TOKEN", "Apple ID token is invalid or expired.");

        return await ExternalLoginAsync(
            payload,
            request.ApplicationCode,
            request.DeviceToken,
            DomainConstants.Providers.Apple,
            settings => settings.AllowAppleLogin,
            "APPLE_LOGIN_DISABLED",
            "Apple login is not allowed for this application.",
            "USER_REGISTERED_APPLE",
            "LOGIN_APPLE_SUCCESS",
            ipAddress,
            userAgent,
            ct);
    }

    private async Task<OperationResult<AuthResponse>> ExternalLoginAsync(
        ExternalTokenPayload payload,
        string applicationCode,
        string? deviceToken,
        string provider,
        Func<ApplicationRegistrationSettings, bool> isProviderAllowed,
        string providerDisabledCode,
        string providerDisabledMessage,
        string registeredAuditAction,
        string loginSuccessAuditAction,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        var appSystem = await _applicationService.GetByCodeWithSettingsAsync(applicationCode, ct);
        if (appSystem is null || !appSystem.IsActive)
            return OperationResult<AuthResponse>.Failure("APP_NOT_FOUND", "Application not found or inactive.");

        var settings = appSystem.RegistrationSettings;
        if (settings is null || !isProviderAllowed(settings))
            return OperationResult<AuthResponse>.Failure(providerDisabledCode, providerDisabledMessage);

        if (!IsEmailDomainAllowed(payload.Email, settings.AllowedEmailDomains))
            return OperationResult<AuthResponse>.Failure("EMAIL_DOMAIN_NOT_ALLOWED", "Email domain is not allowed for this application.");

        var now = _dateTimeProvider.UtcNow;
        var externalProvider = await _db.ExternalIdentityProviders
            .Include(e => e.User)
            .FirstOrDefaultAsync(e => e.Provider == provider && e.ProviderUserId == payload.Subject, ct);

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
                    Provider = provider,
                    ProviderUserId = payload.Subject,
                    Email = payload.Email,
                    DisplayName = payload.Name,
                    PictureUrl = payload.PictureUrl,
                    LinkedAt = now,
                    LastUsedAt = now,
                    IsActive = true
                });
                await _db.SaveChangesAsync(ct);

                var hasExistingAccess = await _userAccessService.HasActiveAccessAsync(user.Id, appSystem.Id, ct);
                if (!hasExistingAccess)
                {
                    await GrantExternalUserAccessAsync(user, appSystem.Id, settings, ct);

                    if (settings.RegistrationMode == ApplicationRegistrationMode.ApprovalRequired)
                        return OperationResult<AuthResponse>.Failure("APPROVAL_REQUIRED", "Your registration is pending approval.");
                }
            }
            else
            {
                if (settings.RegistrationMode is ApplicationRegistrationMode.Closed or ApplicationRegistrationMode.InviteOnly)
                    return OperationResult<AuthResponse>.Failure("REGISTRATION_CLOSED", "Self-registration is not allowed for this application.");

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
                    Provider = provider,
                    ProviderUserId = payload.Subject,
                    Email = payload.Email,
                    DisplayName = payload.Name,
                    PictureUrl = payload.PictureUrl,
                    LinkedAt = now,
                    LastUsedAt = now,
                    IsActive = true
                });

                await GrantExternalUserAccessAsync(user, appSystem.Id, settings, ct);
                await _db.SaveChangesAsync(ct);
                await _auditService.LogAsync(registeredAuditAction, user.Id, appSystem.Code, nameof(ApplicationUser), user.Id.ToString(), ipAddress, userAgent, ct: ct);

                if (settings.RegistrationMode == ApplicationRegistrationMode.ApprovalRequired)
                    return OperationResult<AuthResponse>.Failure("APPROVAL_REQUIRED", "Your registration is pending approval.");
            }
        }
        else
        {
            externalProvider!.LastUsedAt = now;
            if (payload.PictureUrl is not null)
            {
                externalProvider.PictureUrl = payload.PictureUrl;
                user.PictureUrl = payload.PictureUrl;
            }

            if (payload.Name is not null)
                externalProvider.DisplayName = payload.Name;

            await _db.SaveChangesAsync(ct);
        }

        if (!user.IsActive || user.DeletedAt is not null)
            return OperationResult<AuthResponse>.Failure("USER_INACTIVE", "Your account is inactive.");

        var hasAccess = await _userAccessService.HasActiveAccessAsync(user.Id, appSystem.Id, ct);
        if (!hasAccess)
            return OperationResult<AuthResponse>.Failure("ACCESS_DENIED", "You do not have access to this application.");

        var mfaResult = await RequireMfaIfNeededAsync(user.Id, appSystem, deviceToken, ipAddress, userAgent, ct);
        if (mfaResult is not null)
            return mfaResult;

        user.LastLoginAt = now;
        user.UpdatedAt = now;
        await _userManager.UpdateAsync(user);

        await _auditService.LogAsync(loginSuccessAuditAction, user.Id, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
        return await BuildAuthResponseAsync(user, appSystem.Id, appSystem.Code, ipAddress, userAgent, ct);
    }

    private async Task GrantExternalUserAccessAsync(
        ApplicationUser user,
        Guid appSystemId,
        ApplicationRegistrationSettings settings,
        CancellationToken ct)
    {
        var accessIsActive = settings.RegistrationMode == ApplicationRegistrationMode.Open;
        await _userAccessService.GrantAccessAsync(user.Id, appSystemId, accessIsActive, ct);

        if (settings.DefaultRoleId.HasValue)
        {
            var role = await _db.Roles.FindAsync([settings.DefaultRoleId.Value], ct);
            if (role?.Name is not null)
                await _userManager.AddToRoleAsync(user, role.Name);
        }
    }

    public async Task<OperationResult<AuthResponse>> VerifyMfaAsync(VerifyMfaRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var pending = _tokenService.ValidateMfaPendingToken(request.MfaPendingToken);
        if (pending is null)
            return OperationResult<AuthResponse>.Failure("INVALID_MFA_TOKEN", "MFA token is invalid or expired.");

        var cacheKey = GetPendingCacheKey("mfa", pending.TokenId);
        if (_memoryCache.TryGetValue(cacheKey, out _))
            return OperationResult<AuthResponse>.Failure("TOKEN_ALREADY_USED", "MFA token has already been used.");

        _memoryCache.Set(cacheKey, true, TimeSpan.FromSeconds(_mfaSettings.MfaTokenExpirySeconds));

        var user = await _userManager.FindByIdAsync(pending.UserId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult<AuthResponse>.Failure("USER_INACTIVE", "User account is inactive.");

        var appSystem = await _applicationService.GetByCodeAsync(pending.ApplicationCode, ct);
        if (appSystem is null || !appSystem.IsActive)
            return OperationResult<AuthResponse>.Failure("APP_NOT_FOUND", "Application not found or inactive.");

        var hasAccess = await _userAccessService.HasActiveAccessAsync(user.Id, appSystem.Id, ct);
        if (!hasAccess)
            return OperationResult<AuthResponse>.Failure("ACCESS_DENIED", "You do not have access to this application.");

        var verified = false;
        if (!string.IsNullOrWhiteSpace(request.TotpCode))
            verified = await _mfaService.VerifyTotpCodeAsync(user.Id, request.TotpCode, ct);

        if (!verified && !string.IsNullOrWhiteSpace(request.BackupCode))
            verified = await _mfaService.UseBackupCodeAsync(user.Id, request.BackupCode, ct);

        if (!verified && !string.IsNullOrWhiteSpace(request.EmailOtpCode))
        {
            var otpCacheKey = $"emailotp_verify:{pending.TokenId}";
            if (_memoryCache.TryGetValue(otpCacheKey, out string? storedOtp) &&
                storedOtp == request.EmailOtpCode.Trim())
            {
                _memoryCache.Remove(otpCacheKey);
                verified = true;
            }
        }

        if (!verified)
        {
            await _auditService.LogAsync("MFA_VERIFY_FAILED", user.Id, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
            return OperationResult<AuthResponse>.Failure("INVALID_MFA_CODE", "The MFA code is invalid.");
        }

        user.LastLoginAt = _dateTimeProvider.UtcNow;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);

        await _auditService.LogAsync("MFA_VERIFY_SUCCESS", user.Id, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
        await _auditService.LogAsync("LOGIN_SUCCESS", user.Id, appSystem.Code, null, null, ipAddress, userAgent, new { mfa = true }, ct);
        string? deviceToken = null;
        if (request.TrustDevice)
            deviceToken = await CreateTrustedDeviceAsync(user.Id, userAgent, ct);

        return await BuildAuthResponseAsync(user, appSystem.Id, appSystem.Code, ipAddress, userAgent, deviceToken, ct);
    }

    public async Task<OperationResult<AuthResponse>> ForcedChangePasswordAsync(ForcedChangePasswordRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var pending = _tokenService.ValidateForcedChangePendingToken(request.ForcedChangePendingToken);
        if (pending is null)
            return OperationResult<AuthResponse>.Failure("INVALID_FORCED_CHANGE_TOKEN", "Password change token is invalid or expired.");

        var cacheKey = GetPendingCacheKey("forced_change", pending.TokenId);
        if (_memoryCache.TryGetValue(cacheKey, out _))
            return OperationResult<AuthResponse>.Failure("TOKEN_ALREADY_USED", "Password change token has already been used.");

        _memoryCache.Set(cacheKey, true, TimeSpan.FromSeconds(_mfaSettings.MfaTokenExpirySeconds));

        var user = await _userManager.FindByIdAsync(pending.UserId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult<AuthResponse>.Failure("USER_INACTIVE", "User account is inactive.");

        if (!user.HasLocalPassword)
            return OperationResult<AuthResponse>.Failure("NO_LOCAL_PASSWORD", "Account uses external login; password cannot be changed.");

        var appSystem = await _applicationService.GetByCodeAsync(pending.ApplicationCode, ct);
        if (appSystem is null || !appSystem.IsActive)
            return OperationResult<AuthResponse>.Failure("APP_NOT_FOUND", "Application not found or inactive.");

        var hasAccess = await _userAccessService.HasActiveAccessAsync(user.Id, appSystem.Id, ct);
        if (!hasAccess)
            return OperationResult<AuthResponse>.Failure("ACCESS_DENIED", "You do not have access to this application.");

        var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
        var resetResult = await _userManager.ResetPasswordAsync(user, resetToken, request.NewPassword);
        if (!resetResult.Succeeded)
        {
            var errors = resetResult.Errors.Select(e => e.Description).ToList();
            return OperationResult<AuthResponse>.Failure("WEAK_PASSWORD", string.Join("; ", errors), errors);
        }

        user.MustChangePassword = false;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);

        await _refreshTokenService.RevokeAllForUserAsync(user.Id, ct);
        await _auditService.LogAsync("FORCED_PASSWORD_CHANGED", user.Id, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);

        return await BuildAuthResponseAsync(user, appSystem.Id, appSystem.Code, ipAddress, userAgent, ct);
    }

    public async Task<OperationResult<AuthResponse>> RefreshTokenAsync(string refreshToken, string? applicationCode, string? ipAddress, string? userAgent, CancellationToken ct = default)
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
            await _refreshTokenService.RevokeAllForUserAsync(storedToken.UserId, storedToken.ApplicationCode, ct);
            await _auditService.LogAsync("REFRESH_TOKEN_REUSE_DETECTED", storedToken.UserId, storedToken.ApplicationCode, null, null, ipAddress, userAgent, ct: ct);
            return OperationResult<AuthResponse>.Failure("TOKEN_REUSE_DETECTED", "Refresh token has already been used.");
        }

        if (_dateTimeProvider.UtcNow >= storedToken.ExpiresAt)
        {
            await _auditService.LogAsync("REFRESH_TOKEN_EXPIRED", storedToken.UserId, null, null, null, ipAddress, userAgent, ct: ct);
            return OperationResult<AuthResponse>.Failure("TOKEN_EXPIRED", "Refresh token has expired.");
        }

        var appSystem = await _applicationService.GetByCodeAsync(applicationCode ?? storedToken.ApplicationCode, ct);
        if (appSystem is null || !appSystem.IsActive)
            return OperationResult<AuthResponse>.Failure("APP_NOT_FOUND", "Application not found or inactive.");

        if (!string.Equals(storedToken.ApplicationCode, appSystem.Code, StringComparison.Ordinal))
            return OperationResult<AuthResponse>.Failure("TOKEN_APP_MISMATCH", "Refresh token was issued for another application.");

        if (!storedToken.User.IsActive)
            return OperationResult<AuthResponse>.Failure("USER_INACTIVE", "User account is inactive.");

        var hasAccess = await _userAccessService.HasActiveAccessAsync(storedToken.UserId, appSystem.Id, ct);
        if (!hasAccess)
            return OperationResult<AuthResponse>.Failure("ACCESS_DENIED", "You do not have access to this application.");

        var (newToken, newHash) = _tokenService.GenerateRefreshToken();
        await _refreshTokenService.RevokeAsync(storedToken, newHash, ct);
        await _refreshTokenService.CreateAsync(storedToken.UserId, appSystem.Code, newHash, ipAddress, userAgent, ct);

        var user = storedToken.User;
        user.LastLoginAt = _dateTimeProvider.UtcNow;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);

        var apps = new List<string> { appSystem.Code };
        var roles = await _roleService.GetRoleNamesForUserAsync(user.Id, appSystem.Id, ct);
        var permissions = await _roleService.GetPermissionCodesForUserAsync(user.Id, appSystem.Id, ct);

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
        string? applicationCode = null;

        if (refreshToken is not null)
        {
            var tokenHash = _tokenService.HashToken(refreshToken);
            var storedToken = await _refreshTokenService.FindByHashAsync(tokenHash, ct);
            if (storedToken?.UserId == userId && storedToken.RevokedAt is null)
            {
                applicationCode = storedToken.ApplicationCode;
                await _refreshTokenService.RevokeAsync(storedToken, null, ct);
            }
        }

        await _auditService.LogAsync("LOGOUT", userId, applicationCode, ct: ct);
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
        await _auditService.LogAsync("TOKEN_REVOKED", requestingUserId, storedToken.ApplicationCode, ct: ct);
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
        user.HasLocalPassword = true;
        // Receiving a password reset proves inbox ownership — treat as equivalent to email confirmation
        if (!user.EmailConfirmed)
            user.EmailConfirmed = true;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);
        await _refreshTokenService.RevokeAllForUserAsync(user.Id, ct);
        await _auditService.LogAsync("PASSWORD_RESET", user.Id, null, null, null, ipAddress, null, ct: ct);

        return OperationResult.Success();
    }

    public async Task<OperationResult> ConfirmEmailAsync(ConfirmEmailRequest request, string? ipAddress, CancellationToken ct = default)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
            return OperationResult.Failure("INVALID_TOKEN", "Email confirmation failed.");

        var result = await _userManager.ConfirmEmailAsync(user, request.Token);
        if (!result.Succeeded)
            return OperationResult.Failure("INVALID_TOKEN", "Email confirmation failed.");

        await _auditService.LogAsync("EMAIL_CONFIRMED", user.Id, ipAddress: ipAddress, ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ResendEmailConfirmationAsync(ResendEmailConfirmationRequest request, string? ipAddress, CancellationToken ct = default)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null || user.EmailConfirmed)
            return OperationResult.Success();

        var appSystem = await _applicationService.GetByCodeWithSettingsAsync(request.ApplicationCode, ct);
        if (appSystem is null || appSystem.RegistrationSettings?.RequireEmailConfirmation != true)
            return OperationResult.Success();

        var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        await _emailService.SendEmailConfirmationAsync(user.Email!, user.FullName, token, request.CallbackBaseUrl, ct);
        await _auditService.LogAsync("EMAIL_CONFIRMATION_RESENT", user.Id, request.ApplicationCode, ipAddress: ipAddress, ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> SendMagicLinkAsync(MagicLinkRequest request, string? ipAddress, CancellationToken ct = default)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);

        if (user is null || !user.IsActive || user.DeletedAt is not null)
        {
            await _auditService.LogAsync("MAGIC_LINK_NOOP", null, request.ApplicationCode, null, null, ipAddress, null, new { email = request.Email }, ct);
            return OperationResult.Success();
        }

        var appSystem = await _applicationService.GetByCodeWithSettingsAsync(request.ApplicationCode, ct);
        if (appSystem is null || !appSystem.IsActive || appSystem.RegistrationSettings?.AllowMagicLink != true)
        {
            await _auditService.LogAsync("MAGIC_LINK_NOOP", user.Id, request.ApplicationCode, null, null, ipAddress, null, new { reason = "NotAllowed" }, ct);
            return OperationResult.Success();
        }

        var hasAccess = await _userAccessService.HasActiveAccessAsync(user.Id, appSystem.Id, ct);
        if (!hasAccess)
        {
            await _auditService.LogAsync("MAGIC_LINK_NOOP", user.Id, request.ApplicationCode, null, null, ipAddress, null, new { reason = "NoAccess" }, ct);
            return OperationResult.Success();
        }

        var token = _tokenService.GenerateMagicLinkToken(user.Id, appSystem.Code);
        await _emailService.SendMagicLinkAsync(user.Email!, user.FullName, token, request.CallbackBaseUrl, ct);
        await _auditService.LogAsync("MAGIC_LINK_SENT", user.Id, appSystem.Code, null, null, ipAddress, null, ct: ct);

        return OperationResult.Success();
    }

    public async Task<OperationResult<AuthResponse>> VerifyMagicLinkAsync(VerifyMagicLinkRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var pending = _tokenService.ValidateMagicLinkToken(request.Token);
        if (pending is null || !string.Equals(pending.ApplicationCode, request.ApplicationCode, StringComparison.Ordinal))
            return OperationResult<AuthResponse>.Failure("INVALID_MAGIC_LINK_TOKEN", "Magic link is invalid or expired.");

        var cacheKey = GetPendingCacheKey("magic_link", pending.TokenId);
        if (_memoryCache.TryGetValue(cacheKey, out _))
            return OperationResult<AuthResponse>.Failure("TOKEN_ALREADY_USED", "Magic link has already been used.");

        _memoryCache.Set(cacheKey, true, TimeSpan.FromMinutes(_tokenService.MagicLinkTokenMinutes));

        var user = await _userManager.FindByIdAsync(pending.UserId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult<AuthResponse>.Failure("USER_INACTIVE", "User account is inactive.");

        var appSystem = await _applicationService.GetByCodeWithSettingsAsync(pending.ApplicationCode, ct);
        if (appSystem is null || !appSystem.IsActive || appSystem.RegistrationSettings?.AllowMagicLink != true)
            return OperationResult<AuthResponse>.Failure("APP_NOT_FOUND", "Application not found or inactive.");

        var hasAccess = await _userAccessService.HasActiveAccessAsync(user.Id, appSystem.Id, ct);
        if (!hasAccess)
            return OperationResult<AuthResponse>.Failure("ACCESS_DENIED", "You do not have access to this application.");

        var mfaResult = await RequireMfaIfNeededAsync(user.Id, appSystem, request.DeviceToken, ipAddress, userAgent, ct);
        if (mfaResult is not null)
            return mfaResult;

        user.LastLoginAt = _dateTimeProvider.UtcNow;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        user.EmailConfirmed = true;
        await _userManager.UpdateAsync(user);

        await _auditService.LogAsync("LOGIN_MAGIC_LINK_SUCCESS", user.Id, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
        return await BuildAuthResponseAsync(user, appSystem.Id, appSystem.Code, ipAddress, userAgent, ct);
    }

    public async Task<OperationResult> SendMfaEmailOtpAsync(SendMfaEmailOtpRequest request, CancellationToken ct = default)
    {
        var pending = _tokenService.ValidateMfaPendingToken(request.MfaPendingToken);
        if (pending is null)
            return OperationResult.Failure("INVALID_MFA_TOKEN", "MFA token is invalid or expired.");

        var usedKey = GetPendingCacheKey("mfa", pending.TokenId);
        if (_memoryCache.TryGetValue(usedKey, out _))
            return OperationResult.Failure("TOKEN_ALREADY_USED", "MFA token has already been used.");

        var sent = await _mfaService.SendMfaEmailOtpAsync(pending.UserId, pending.TokenId, ct);
        if (!sent)
            return OperationResult.Failure("EMAIL_OTP_SEND_FAILED", "Failed to send the sign-in code.");

        return OperationResult.Success();
    }

    private async Task<OperationResult<AuthResponse>?> RequireMfaIfNeededAsync(
        Guid userId,
        ApplicationSystem appSystem,
        string? deviceToken,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        var mfaCredential = await _db.UserMfaCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == userId, ct);

        var mfaRequired = appSystem.RegistrationSettings?.RequireMfa == true || mfaCredential?.IsEnabled == true;
        if (!mfaRequired)
            return null;

        if (mfaCredential?.IsEnabled != true)
        {
            await _auditService.LogAsync("MFA_SETUP_REQUIRED", userId, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
            return OperationResult<AuthResponse>.Failure(
                "MFA_SETUP_REQUIRED",
                "This application requires MFA. Please set up two-factor authentication.");
        }

        if (!string.IsNullOrWhiteSpace(deviceToken))
        {
            var hash = _tokenService.HashToken(deviceToken);
            var now = _dateTimeProvider.UtcNow;
            var trusted = await _db.UserTrustedDevices
                .FirstOrDefaultAsync(d => d.UserId == userId && d.TokenHash == hash && d.ExpiresAt > now, ct);

            if (trusted is not null)
            {
                trusted.LastUsedAt = now;
                await _db.SaveChangesAsync(ct);
                await _auditService.LogAsync("MFA_SKIPPED_TRUSTED_DEVICE", userId, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
                return null;
            }
        }

        var pendingToken = _tokenService.GenerateMfaPendingToken(userId, appSystem.Code);
        await _auditService.LogAsync("MFA_REQUIRED", userId, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
        return OperationResult<AuthResponse>.Failure("MFA_REQUIRED", pendingToken);
    }

    private async Task<OperationResult<AuthResponse>> BuildAuthResponseAsync(ApplicationUser user, Guid appSystemId, string appCode, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        return await BuildAuthResponseAsync(user, appSystemId, appCode, ipAddress, userAgent, null, ct);
    }

    private async Task<OperationResult<AuthResponse>> BuildAuthResponseAsync(ApplicationUser user, Guid appSystemId, string appCode, string? ipAddress, string? userAgent, string? deviceToken, CancellationToken ct)
    {
        var apps = new List<string> { appCode };
        var roles = await _roleService.GetRoleNamesForUserAsync(user.Id, appSystemId, ct);
        var permissions = await _roleService.GetPermissionCodesForUserAsync(user.Id, appSystemId, ct);

        var accessToken = _tokenService.GenerateAccessToken(user, roles, permissions, apps);
        var (rawRefresh, refreshHash) = _tokenService.GenerateRefreshToken();
        await _refreshTokenService.CreateAsync(user.Id, appCode, refreshHash, ipAddress, userAgent, ct);

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
            },
            DeviceToken = deviceToken
        });
    }

    private async Task<string> CreateTrustedDeviceAsync(Guid userId, string? userAgent, CancellationToken ct)
    {
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var now = _dateTimeProvider.UtcNow;

        _db.UserTrustedDevices.Add(new UserTrustedDevice
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = _tokenService.HashToken(rawToken),
            DeviceName = DeriveDeviceName(userAgent),
            CreatedAt = now,
            ExpiresAt = now.AddDays(30)
        });
        await _db.SaveChangesAsync(ct);
        await _auditService.LogAsync("TRUSTED_DEVICE_CREATED", userId, ct: ct);

        return rawToken;
    }

    private static string GetPendingCacheKey(string purpose, string tokenId) =>
        $"{purpose}:{tokenId}";

    private static string? DeriveDeviceName(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return null;

        return userAgent.Length <= 100 ? userAgent : userAgent[..100];
    }

    private static bool IsEmailDomainAllowed(string email, string? allowedEmailDomains)
    {
        if (string.IsNullOrWhiteSpace(allowedEmailDomains))
            return true;

        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
            return false;

        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        var allowed = allowedEmailDomains
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.TrimStart('@').ToLowerInvariant());

        return allowed.Contains(domain);
    }
}
