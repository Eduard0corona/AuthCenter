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
    private readonly SignInManager<ApplicationUser> _signInManager;
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
    private readonly IActionLinkService _actionLinkService;
    private readonly IMfaService _mfaService;
    private readonly ITransientStateStore _transientState;
    private readonly MfaSettings _mfaSettings;
    private readonly AuthCenterDbContext _db;
    private readonly ILogger<AuthService> _logger;
    private readonly IAuthenticationSessionIssuer _sessionIssuer;
    private readonly IAccessPolicyService _accessPolicies;
    private readonly IAuthenticationRiskService _authenticationRisk;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
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
        IActionLinkService actionLinkService,
        IMfaService mfaService,
        ITransientStateStore transientState,
        IOptions<MfaSettings> mfaSettings,
        AuthCenterDbContext db,
        ILogger<AuthService> logger,
        IAuthenticationSessionIssuer sessionIssuer,
        IAccessPolicyService accessPolicies,
        IAuthenticationRiskService authenticationRisk)
    {
        _userManager = userManager;
        _signInManager = signInManager;
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
        _actionLinkService = actionLinkService;
        _mfaService = mfaService;
        _transientState = transientState;
        _mfaSettings = mfaSettings.Value;
        _db = db;
        _logger = logger;
        _sessionIssuer = sessionIssuer;
        _accessPolicies = accessPolicies;
        _authenticationRisk = authenticationRisk;
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

        // The account, its access and its default role are created atomically. The sign-in that
        // follows runs after the commit: its services (risk signals, sessions) write through their
        // own connections, which would otherwise wait on the uncommitted user row.
        var strategy = _db.Database.CreateExecutionStrategy();
        var created = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            var now = _dateTimeProvider.UtcNow;
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                FullName = request.FullName,
                Email = request.Email,
                UserName = request.Email,
                // Verified only when its owner follows the confirmation link: an application that
                // lets unconfirmed accounts sign in must not make them assert email_verified.
                EmailConfirmed = false,
                IsExternalUser = false,
                HasLocalPassword = true,
                IsActive = true,
                CreatedAt = now
            };

            await using var transaction = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(ct)
                : null;

            var createResult = await _userManager.CreateAsync(user, request.Password);
            if (!createResult.Succeeded)
            {
                var errors = createResult.Errors.Select(error => error.Description).ToList();
                return (Failure: OperationResult<AuthResponse>.Failure("USER_CREATION_FAILED", string.Join("; ", errors), errors), User: (ApplicationUser?)null);
            }

            var accessIsActive = settings.RegistrationMode == ApplicationRegistrationMode.Open;
            await _userAccessService.GrantAccessAsync(user.Id, appSystem.Id, accessIsActive, ct);

            if (settings.DefaultRoleId.HasValue)
            {
                var role = await _db.Roles.FindAsync([settings.DefaultRoleId.Value], ct);
                if (role?.Name is not null)
                {
                    var roleResult = await _userManager.AddToRoleAsync(user, role.Name);
                    if (!roleResult.Succeeded)
                        return (Failure: OperationResult<AuthResponse>.Failure("ROLE_ASSIGN_FAILED", string.Join("; ", roleResult.Errors.Select(error => error.Description))), User: (ApplicationUser?)null);
                }
            }

            // Sent even when the application does not require it, so that the address can become verified.
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var actionUrl = _actionLinkService.GetActionUrl(ActionLinkPurpose.EmailConfirmation, appSystem.Code);
            await _emailService.SendEmailConfirmationAsync(user.Email!, user.FullName, token, actionUrl, ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return (Failure: (OperationResult<AuthResponse>?)null, User: (ApplicationUser?)user);
        });

        if (created.Failure is not null)
            return created.Failure;
        var registered = created.User!;
        await _auditService.LogAsync("USER_REGISTERED", registered.Id, appSystem.Code, nameof(ApplicationUser), registered.Id.ToString(), ipAddress, userAgent, ct: ct);

        if (settings.RequireEmailConfirmation)
            return OperationResult<AuthResponse>.Failure("EMAIL_CONFIRMATION_REQUIRED", "Please confirm your email before signing in.");
        if (settings.RegistrationMode == ApplicationRegistrationMode.ApprovalRequired)
            return OperationResult<AuthResponse>.Failure("APPROVAL_REQUIRED", "Your registration is pending approval.");

        // The first sign-in goes through the application's gate like any other; when it asks for a
        // factor to enroll or denies the sign-in, the new account is kept.
        var policyResult = await RequireMfaIfNeededAsync(registered.Id, appSystem, null, ipAddress, userAgent, ct);
        return policyResult ?? await BuildAuthResponseAsync(registered, appSystem.Id, appSystem.Code, ipAddress, userAgent, ct);
    }

    public async Task<OperationResult<AuthResponse>> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        // Resolved before the password is checked so that an application restricted to federated or
        // passwordless sign-in never reveals whether a submitted password was correct.
        var appSystem = await _applicationService.GetByCodeWithSettingsAsync(request.ApplicationCode, ct);
        if (appSystem?.RegistrationSettings is { AllowPasswordLogin: false })
        {
            await _auditService.LogAsync("LOGIN_FAILED", null, request.ApplicationCode, null, null, ipAddress, userAgent, new { reason = "PasswordLoginDisabled" }, ct);
            return OperationResult<AuthResponse>.Failure("PASSWORD_LOGIN_DISABLED", "Password sign-in is not allowed for this application.");
        }

        var user = await _userManager.FindByEmailAsync(request.Email);

        if (user is null || !user.IsActive)
        {
            await _auditService.LogAsync("LOGIN_FAILED", null, request.ApplicationCode, null, null, ipAddress, userAgent, new { reason = "UserNotFound" }, ct);
            return OperationResult<AuthResponse>.Failure("INVALID_CREDENTIALS", "Invalid email or password.");
        }

        var passwordResult = await _signInManager.CheckPasswordSignInAsync(
            user,
            request.Password,
            lockoutOnFailure: true);

        if (passwordResult.IsLockedOut)
        {
            await _auditService.LogAsync("LOGIN_LOCKED_OUT", user.Id, request.ApplicationCode, null, null, ipAddress, userAgent, ct: ct);
            return OperationResult<AuthResponse>.Failure("ACCOUNT_LOCKED", "Account temporarily locked due to repeated failed sign-in attempts.");
        }

        if (!passwordResult.Succeeded)
        {
            await _auditService.LogAsync("LOGIN_FAILED", user.Id, request.ApplicationCode, null, null, ipAddress, userAgent, new { reason = "InvalidPassword" }, ct);
            return OperationResult<AuthResponse>.Failure("INVALID_CREDENTIALS", "Invalid email or password.");
        }

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

        await _userManager.RecordSignInAsync(_db, user, _dateTimeProvider.UtcNow, ct, account => account.AccessFailedCount = 0);

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
            PictureUrl = payload.PictureUrl,
            EmailVerified = payload.EmailVerified
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
                // Matching an email is not proof that the external identity controls the existing
                // AuthCenter account. Linking is an authenticated, explicit account-management flow.
                return OperationResult<AuthResponse>.Failure(
                    "EXTERNAL_ACCOUNT_LINK_REQUIRED",
                    "Sign in with an existing method and explicitly link this external provider.");
            }
            else
            {
                if (settings.RegistrationMode is ApplicationRegistrationMode.Closed or ApplicationRegistrationMode.InviteOnly)
                    return OperationResult<AuthResponse>.Failure("REGISTRATION_CLOSED", "Self-registration is not allowed for this application.");

                var strategy = _db.Database.CreateExecutionStrategy();
                var creation = await strategy.ExecuteAsync(async () =>
                {
                    _db.ChangeTracker.Clear();
                    await using var transaction = _db.Database.IsRelational()
                        ? await _db.Database.BeginTransactionAsync(ct)
                        : null;
                    var newUser = new ApplicationUser
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
                        EmailConfirmed = payload.EmailVerified
                    };

                    var createResult = await _userManager.CreateAsync(newUser);
                    if (!createResult.Succeeded)
                    {
                        var errors = createResult.Errors.Select(error => error.Description).ToList();
                        return (User: (ApplicationUser?)null, Error: OperationResult<AuthResponse>.Failure("USER_CREATION_FAILED", string.Join("; ", errors)));
                    }

                    _db.ExternalIdentityProviders.Add(new ExternalIdentityProvider
                    {
                        Id = Guid.NewGuid(),
                        UserId = newUser.Id,
                        Provider = provider,
                        ProviderUserId = payload.Subject,
                        Email = payload.Email,
                        DisplayName = payload.Name,
                        PictureUrl = payload.PictureUrl,
                        LinkedAt = now,
                        LastUsedAt = now,
                        IsActive = true
                    });

                    await GrantExternalUserAccessAsync(newUser, appSystem.Id, settings, ct);
                    await _db.SaveChangesAsync(ct);
                    if (transaction is not null)
                        await transaction.CommitAsync(ct);
                    return (User: (ApplicationUser?)newUser, Error: (OperationResult<AuthResponse>?)null);
                });

                if (creation.Error is not null)
                    return creation.Error;
                user = creation.User!;
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

        var mfaResult = await RequireMfaIfNeededAsync(user.Id, appSystem, deviceToken, ipAddress, userAgent, ct, DomainConstants.AuthenticationMethods.Federated);
        if (mfaResult is not null)
            return mfaResult;

        await _userManager.RecordSignInAsync(_db, user, now, ct);

        await _auditService.LogAsync(loginSuccessAuditAction, user.Id, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
        return await BuildAuthResponseAsync(user, appSystem.Id, appSystem.Code, ipAddress, userAgent, null, AuthenticationContext.Federated, ct);
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

        // A pending sign-in allows a few attempts, so a mistyped code does not mean typing the
        // password again; it is spent by the first success or by the last failed attempt.
        var stepExpiry = _dateTimeProvider.UtcNow.AddSeconds(_mfaSettings.MfaTokenExpirySeconds);
        if (await _transientState.IsConsumedAsync(SingleUsePurposes.Mfa, pending.TokenId, ct))
            return OperationResult<AuthResponse>.Failure("TOKEN_ALREADY_USED", "MFA token has already been used.");

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
            var storedOtp = await _transientState.GetAsync(
                MfaStatePurposes.EmailOtpVerify, pending.TokenId, ct);

            // Only cleared on a match, so a wrong guess cannot invalidate the real code.
            if (storedOtp is not null && storedOtp == request.EmailOtpCode.Trim())
            {
                await _transientState.RemoveAsync(MfaStatePurposes.EmailOtpVerify, pending.TokenId, ct);
                verified = true;
            }
        }

        if (!verified)
        {
            var attempts = int.TryParse(await _transientState.GetAsync(MfaAttemptsPurpose, pending.TokenId, ct), out var previous) ? previous + 1 : 1;
            if (attempts >= MaximumMfaAttempts)
                await _transientState.TryConsumeAsync(SingleUsePurposes.Mfa, pending.TokenId, stepExpiry, ct);
            else
                await _transientState.SetAsync(MfaAttemptsPurpose, pending.TokenId, attempts.ToString(System.Globalization.CultureInfo.InvariantCulture), stepExpiry, ct);
            await _auditService.LogAsync("MFA_VERIFY_FAILED", user.Id, appSystem.Code, null, null, ipAddress, userAgent, new { attempts }, ct);
            return OperationResult<AuthResponse>.Failure("INVALID_MFA_CODE", "The MFA code is invalid.");
        }

        if (!await _transientState.TryConsumeAsync(SingleUsePurposes.Mfa, pending.TokenId, stepExpiry, ct))
            return OperationResult<AuthResponse>.Failure("TOKEN_ALREADY_USED", "MFA token has already been used.");

        // Re-evaluate the published policy after step-up. A policy may have been published or a
        // time window may have closed while the MFA ceremony was in progress.
        var postMfaPolicy = await _accessPolicies.EvaluateAsync(new AccessPolicyEvaluationContext(
            user.Id,
            appSystem.Id,
            ipAddress,
            _dateTimeProvider.UtcNow,
            AccessRiskLevel.Unknown,
            AuthenticationAssuranceLevel.Mfa), ct);
        if (!postMfaPolicy.IsAllowed)
        {
            await _auditService.LogAsync(
                "ACCESS_POLICY_DENIED_AFTER_MFA",
                user.Id,
                appSystem.Code,
                nameof(ApplicationAccessPolicyRule),
                postMfaPolicy.MatchedRuleId?.ToString(),
                ipAddress,
                userAgent,
                new { postMfaPolicy.MatchedRuleName },
                ct);
            return OperationResult<AuthResponse>.Failure(
                "ACCESS_POLICY_DENIED",
                "Sign-in is denied by the application's access policy.");
        }

        await _userManager.RecordSignInAsync(_db, user, _dateTimeProvider.UtcNow, ct);

        await _auditService.LogAsync("MFA_VERIFY_SUCCESS", user.Id, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
        await _auditService.LogAsync("LOGIN_SUCCESS", user.Id, appSystem.Code, null, null, ipAddress, userAgent, new { mfa = true }, ct);
        string? deviceToken = null;
        if (request.TrustDevice)
            deviceToken = await CreateTrustedDeviceAsync(user.Id, userAgent, ct);

        return await BuildAuthResponseAsync(user, appSystem.Id, appSystem.Code, ipAddress, userAgent, deviceToken, AuthenticationContext.WithSecondFactor(pending.PrimaryMethod), ct);
    }

    public async Task<OperationResult<AuthResponse>> BeginStepUpAsync(
        Guid userId,
        string applicationCode,
        AuthenticationAssuranceLevel requiredAssurance,
        string primaryMethod,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        var appSystem = await _applicationService.GetByCodeAsync(applicationCode, ct);
        if (appSystem is null || !appSystem.IsActive)
            return OperationResult<AuthResponse>.Failure("APP_NOT_FOUND", "Application not found or inactive.");

        if (requiredAssurance >= AuthenticationAssuranceLevel.PhishingResistant)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            var hasPasskey = user is not null && (await _userManager.GetPasskeysAsync(user)).Count > 0;
            var code = hasPasskey ? "PASSKEY_REQUIRED" : "PASSKEY_ENROLLMENT_REQUIRED";
            await _auditService.LogAsync(code, userId, appSystem.Code, null, null, ipAddress, userAgent, new { stepUp = true }, ct);
            // As at sign-in, a missing factor comes with a single-use enrollment for the hosted login.
            return OperationResult<AuthResponse>.Failure(
                code,
                hasPasskey
                    ? "This application requires a user-verified passkey. Continue with passkey sign-in."
                    : await CreateEnrollmentAsync(userId, appSystem.Code, primaryMethod, EnrollmentKinds.Passkey, ct));
        }

        var mfaEnabled = await _db.UserMfaCredentials.AsNoTracking().AnyAsync(m => m.UserId == userId && m.IsEnabled, ct);
        if (!mfaEnabled)
        {
            await _auditService.LogAsync("MFA_SETUP_REQUIRED", userId, appSystem.Code, null, null, ipAddress, userAgent, new { stepUp = true }, ct);
            return OperationResult<AuthResponse>.Failure("MFA_SETUP_REQUIRED", await CreateEnrollmentAsync(userId, appSystem.Code, primaryMethod, EnrollmentKinds.Totp, ct));
        }

        // The pending token carries the session's primary method, so the verified step-up records
        // both factors (for example pwd + otp) on the continued single sign-on session.
        var pendingToken = _tokenService.GenerateMfaPendingToken(userId, appSystem.Code, primaryMethod);
        await _auditService.LogAsync("MFA_STEP_UP_REQUIRED", userId, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
        return OperationResult<AuthResponse>.Failure("MFA_REQUIRED", pendingToken);
    }

    public async Task<OperationResult<AuthResponse>> CompleteFederatedSignInAsync(
        Guid userId,
        string applicationCode,
        AuthenticationContext authentication,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult<AuthResponse>.Failure("USER_INACTIVE", "Your account is inactive.");

        var appSystem = await _applicationService.GetByCodeWithSettingsAsync(applicationCode, ct);
        if (appSystem is null || !appSystem.IsActive)
            return OperationResult<AuthResponse>.Failure("APP_NOT_FOUND", "Application not found or inactive.");

        if (!await _userAccessService.HasActiveAccessAsync(user.Id, appSystem.Id, ct))
        {
            await _auditService.LogAsync("LOGIN_FAILED", user.Id, appSystem.Code, null, null, ipAddress, userAgent, new { reason = "NoAccess", method = "federated" }, ct);
            return OperationResult<AuthResponse>.Failure("ACCESS_DENIED", "You do not have access to this application.");
        }

        var primaryMethod = authentication.Methods.FirstOrDefault() ?? DomainConstants.AuthenticationMethods.Federated;
        var gate = await RequireMfaIfNeededAsync(user.Id, appSystem, null, ipAddress, userAgent, ct, primaryMethod, authentication.Assurance);
        if (gate is not null)
            return gate;

        await _userManager.RecordSignInAsync(_db, user, _dateTimeProvider.UtcNow, ct);

        await _auditService.LogAsync("LOGIN_SUCCESS", user.Id, appSystem.Code, null, null, ipAddress, userAgent, new { method = "federated", amr = authentication.MethodsValue }, ct);
        return await BuildAuthResponseAsync(user, appSystem.Id, appSystem.Code, ipAddress, userAgent, null, authentication, ct);
    }

    public async Task<OperationResult<AuthResponse>> ForcedChangePasswordAsync(ForcedChangePasswordRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var pending = _tokenService.ValidateForcedChangePendingToken(request.ForcedChangePendingToken);
        if (pending is null)
            return OperationResult<AuthResponse>.Failure("INVALID_FORCED_CHANGE_TOKEN", "Password change token is invalid or expired.");

        var consumed = await _transientState.TryConsumeAsync(
            SingleUsePurposes.ForcedChange,
            pending.TokenId,
            _dateTimeProvider.UtcNow.AddSeconds(_mfaSettings.MfaTokenExpirySeconds),
            ct);

        if (!consumed)
            return OperationResult<AuthResponse>.Failure("TOKEN_ALREADY_USED", "Password change token has already been used.");

        var user = await _userManager.FindByIdAsync(pending.UserId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult<AuthResponse>.Failure("USER_INACTIVE", "User account is inactive.");

        if (!user.HasLocalPassword)
            return OperationResult<AuthResponse>.Failure("NO_LOCAL_PASSWORD", "Account uses external login; password cannot be changed.");

        var appSystem = await _applicationService.GetByCodeWithSettingsAsync(pending.ApplicationCode, ct);
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

        // The password step of the original sign-in ended before its access-policy and MFA gate, so
        // the same gate runs here; changing a password must never become a way around MFA.
        var gate = await RequireMfaIfNeededAsync(user.Id, appSystem, null, ipAddress, userAgent, ct);
        if (gate is not null)
            return gate;

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

        var policy = await _accessPolicies.EvaluateAsync(storedToken.UserId, appSystem.Id, ipAddress, ct);
        if (!policy.IsAllowed)
        {
            await _refreshTokenService.RevokeAllForUserAsync(storedToken.UserId, appSystem.Code, ct);
            await _auditService.LogAsync(
                "ACCESS_POLICY_DENIED",
                storedToken.UserId,
                appSystem.Code,
                nameof(ApplicationAccessPolicyRule),
                policy.MatchedRuleId?.ToString(),
                ipAddress,
                userAgent,
                new { policy.MatchedRuleName, flow = "refresh" },
                ct);
            return OperationResult<AuthResponse>.Failure("ACCESS_POLICY_DENIED", "Sign-in is denied by the application's access policy.");
        }

        var (newToken, newHash) = _tokenService.GenerateRefreshToken();
        var replacementTokenId = Guid.NewGuid();
        var rotated = await _refreshTokenService.TryRotateAsync(
            storedToken,
            replacementTokenId,
            newHash,
            ipAddress,
            userAgent,
            ct);
        if (!rotated)
        {
            await _refreshTokenService.RevokeAllForUserAsync(storedToken.UserId, storedToken.ApplicationCode, ct);
            await _auditService.LogAsync("REFRESH_TOKEN_CONCURRENT_REUSE_DETECTED", storedToken.UserId, storedToken.ApplicationCode, ipAddress: ipAddress, userAgent: userAgent, ct: ct);
            return OperationResult<AuthResponse>.Failure("TOKEN_REUSE_DETECTED", "Refresh token was already used by another request.");
        }

        var user = storedToken.User;
        await _userManager.RecordSignInAsync(_db, user, _dateTimeProvider.UtcNow, ct);

        var apps = new List<string> { appSystem.Code };
        var roles = await _roleService.GetRoleNamesForUserAsync(user.Id, appSystem.Id, ct);
        var permissions = await _roleService.GetPermissionCodesForUserAsync(user.Id, appSystem.Id, ct);

        var accessToken = _tokenService.GenerateAccessToken(user, roles, permissions, apps, replacementTokenId);

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

        var actionUrl = _actionLinkService.GetActionUrl(ActionLinkPurpose.PasswordReset, request.ApplicationCode);
        await _emailService.SendPasswordResetAsync(user.Email!, user.FullName, token, actionUrl, ct);
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
            // Identity checks the token before the password, so policy errors mean a valid link.
            return result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.InvalidToken))
                ? OperationResult.Failure("INVALID_TOKEN", "Password reset failed. The link may be expired or invalid.")
                : OperationResult.Failure("WEAK_PASSWORD", string.Join(" ", errors));
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
        if (user is null || user.EmailConfirmed || !user.IsActive || user.DeletedAt is not null)
            return OperationResult.Success();

        var appSystem = await _applicationService.GetByCodeWithSettingsAsync(request.ApplicationCode, ct);
        if (appSystem is null || !appSystem.IsActive)
            return OperationResult.Success();

        var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        var actionUrl = _actionLinkService.GetActionUrl(ActionLinkPurpose.EmailConfirmation, request.ApplicationCode);
        await _emailService.SendEmailConfirmationAsync(user.Email!, user.FullName, token, actionUrl, ct);
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
        var actionUrl = _actionLinkService.GetActionUrl(ActionLinkPurpose.MagicLink, request.ApplicationCode);
        await _emailService.SendMagicLinkAsync(user.Email!, user.FullName, token, actionUrl, ct);
        await _auditService.LogAsync("MAGIC_LINK_SENT", user.Id, appSystem.Code, null, null, ipAddress, null, ct: ct);

        return OperationResult.Success();
    }

    public async Task<OperationResult<AuthResponse>> VerifyMagicLinkAsync(VerifyMagicLinkRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var pending = _tokenService.ValidateMagicLinkToken(request.Token);
        if (pending is null || !string.Equals(pending.ApplicationCode, request.ApplicationCode, StringComparison.Ordinal))
            return OperationResult<AuthResponse>.Failure("INVALID_MAGIC_LINK_TOKEN", "Magic link is invalid or expired.");

        var consumed = await _transientState.TryConsumeAsync(
            SingleUsePurposes.MagicLink,
            pending.TokenId,
            _dateTimeProvider.UtcNow.AddMinutes(_tokenService.MagicLinkTokenMinutes),
            ct);

        if (!consumed)
            return OperationResult<AuthResponse>.Failure("TOKEN_ALREADY_USED", "Magic link has already been used.");

        var user = await _userManager.FindByIdAsync(pending.UserId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult<AuthResponse>.Failure("USER_INACTIVE", "User account is inactive.");

        var appSystem = await _applicationService.GetByCodeWithSettingsAsync(pending.ApplicationCode, ct);
        if (appSystem is null || !appSystem.IsActive || appSystem.RegistrationSettings?.AllowMagicLink != true)
            return OperationResult<AuthResponse>.Failure("APP_NOT_FOUND", "Application not found or inactive.");

        var hasAccess = await _userAccessService.HasActiveAccessAsync(user.Id, appSystem.Id, ct);
        if (!hasAccess)
            return OperationResult<AuthResponse>.Failure("ACCESS_DENIED", "You do not have access to this application.");

        var mfaResult = await RequireMfaIfNeededAsync(user.Id, appSystem, request.DeviceToken, ipAddress, userAgent, ct, DomainConstants.AuthenticationMethods.OneTimePassword);
        if (mfaResult is not null)
            return mfaResult;

        await _userManager.RecordSignInAsync(_db, user, _dateTimeProvider.UtcNow, ct, account => account.EmailConfirmed = true);

        await _auditService.LogAsync("LOGIN_MAGIC_LINK_SUCCESS", user.Id, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
        return await BuildAuthResponseAsync(user, appSystem.Id, appSystem.Code, ipAddress, userAgent, null, AuthenticationContext.OneTimeLink, ct);
    }

    public async Task<OperationResult> SendMfaEmailOtpAsync(SendMfaEmailOtpRequest request, CancellationToken ct = default)
    {
        var pending = _tokenService.ValidateMfaPendingToken(request.MfaPendingToken);
        if (pending is null)
            return OperationResult.Failure("INVALID_MFA_TOKEN", "MFA token is invalid or expired.");

        if (await _transientState.IsConsumedAsync(SingleUsePurposes.Mfa, pending.TokenId, ct))
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
        CancellationToken ct,
        string primaryMethod = DomainConstants.AuthenticationMethods.Password,
        AuthenticationAssuranceLevel reached = AuthenticationAssuranceLevel.Password)
    {
        var signals = await _authenticationRisk.AssessAndRecordAsync(userId, ipAddress, userAgent, ct: ct);
        var policy = await _accessPolicies.EvaluateAsync(new AccessPolicyEvaluationContext(
            userId,
            appSystem.Id,
            ipAddress,
            _dateTimeProvider.UtcNow,
            signals.RiskLevel,
            reached), ct);
        if (!policy.IsAllowed)
        {
            await _auditService.LogAsync(
                "ACCESS_POLICY_DENIED",
                userId,
                appSystem.Code,
                nameof(ApplicationAccessPolicyRule),
                policy.MatchedRuleId?.ToString(),
                ipAddress,
                userAgent,
                new { policy.MatchedRuleName },
                ct);
            return OperationResult<AuthResponse>.Failure(
                "ACCESS_POLICY_DENIED",
                "Sign-in is denied by the application's access policy.");
        }

        if (policy.RequiredAssuranceLevel == AuthenticationAssuranceLevel.PhishingResistant && reached < AuthenticationAssuranceLevel.PhishingResistant)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            var hasPasskey = user is not null && (await _userManager.GetPasskeysAsync(user)).Count > 0;
            await _auditService.LogAsync(
                hasPasskey ? "PASSKEY_REQUIRED" : "PASSKEY_ENROLLMENT_REQUIRED",
                userId,
                appSystem.Code,
                nameof(ApplicationAccessPolicyRule),
                policy.MatchedRuleId?.ToString(),
                ipAddress,
                userAgent,
                ct: ct);
            // Like MFA_REQUIRED, the message carries what the next step needs: here a single-use
            // enrollment token, so the hosted login can register the passkey right away.
            return hasPasskey
                ? OperationResult<AuthResponse>.Failure("PASSKEY_REQUIRED", "This application requires a user-verified passkey. Continue with passkey sign-in.")
                : OperationResult<AuthResponse>.Failure("PASSKEY_ENROLLMENT_REQUIRED", await CreateEnrollmentAsync(userId, appSystem.Code, primaryMethod, EnrollmentKinds.Passkey, ct));
        }

        var mfaCredential = await _db.UserMfaCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == userId, ct);

        var mfaRequired = policy.RequireMfa || appSystem.RegistrationSettings?.RequireMfa == true || mfaCredential?.IsEnabled == true;
        // A trusted upstream MFA (federation) already satisfies the second factor.
        if (!mfaRequired || reached >= AuthenticationAssuranceLevel.Mfa)
            return null;

        if (mfaCredential?.IsEnabled != true)
        {
            await _auditService.LogAsync("MFA_SETUP_REQUIRED", userId, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
            return OperationResult<AuthResponse>.Failure("MFA_SETUP_REQUIRED", await CreateEnrollmentAsync(userId, appSystem.Code, primaryMethod, EnrollmentKinds.Totp, ct));
        }

        if (policy.AllowTrustedDeviceBypass && !string.IsNullOrWhiteSpace(deviceToken))
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

        var pendingToken = _tokenService.GenerateMfaPendingToken(userId, appSystem.Code, primaryMethod);
        await _auditService.LogAsync("MFA_REQUIRED", userId, appSystem.Code, null, null, ipAddress, userAgent, ct: ct);
        return OperationResult<AuthResponse>.Failure("MFA_REQUIRED", pendingToken);
    }

    public async Task<OperationResult<MfaSetupResponse>> BeginTotpEnrollmentAsync(string enrollmentToken, CancellationToken ct = default)
    {
        var enrollment = await ReadEnrollmentAsync(enrollmentToken, EnrollmentKinds.Totp, ct);
        if (enrollment is null)
            return OperationResult<MfaSetupResponse>.Failure("INVALID_ENROLLMENT", "The enrollment expired or was already used. Sign in again.");
        if (await _db.UserMfaCredentials.AsNoTracking().AnyAsync(item => item.UserId == enrollment.UserId && item.IsEnabled, ct))
            return OperationResult<MfaSetupResponse>.Failure("MFA_ALREADY_ENABLED", "Two-factor authentication is already enabled. Sign in again.");
        return await _mfaService.SetupTotpAsync(enrollment.UserId, ct);
    }

    public async Task<OperationResult<MfaEnrollmentResult>> CompleteTotpEnrollmentAsync(string enrollmentToken, string totpCode, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var enrollment = await ReadEnrollmentAsync(enrollmentToken, EnrollmentKinds.Totp, ct);
        if (enrollment is null)
            return OperationResult<MfaEnrollmentResult>.Failure("INVALID_ENROLLMENT", "The enrollment expired or was already used. Sign in again.");

        // A wrong code leaves the enrollment usable, so the user can type the next one.
        var enabled = await _mfaService.EnableTotpAsync(enrollment.UserId, new EnableMfaRequest { TotpCode = totpCode }, ct);
        if (!enabled.IsSuccess)
            return OperationResult<MfaEnrollmentResult>.Failure(enabled.ErrorCode!, enabled.Message!);
        await _transientState.TakeAsync(EnrollmentPurpose, enrollmentToken, ct);

        var user = await _userManager.FindByIdAsync(enrollment.UserId.ToString());
        var appSystem = await _applicationService.GetByCodeWithSettingsAsync(enrollment.ApplicationCode, ct);
        if (user is null || !user.IsActive || user.DeletedAt is not null || appSystem is null || !appSystem.IsActive)
            return OperationResult<MfaEnrollmentResult>.Failure("USER_INACTIVE", "The account or the application is no longer active.");
        await _auditService.LogAsync("MFA_ENROLLED_AT_SIGN_IN", user.Id, appSystem.Code, null, null, ipAddress, userAgent, new { method = "totp" }, ct);
        await _emailService.SendSecurityNoticeAsync(user.Email!, user.FullName, "Two-step verification enabled",
            "An authenticator app was set up for your account while signing in.", ct);

        if (!await _userAccessService.HasActiveAccessAsync(user.Id, appSystem.Id, ct))
            return OperationResult<MfaEnrollmentResult>.Failure("ACCESS_DENIED", "You do not have access to this application.");
        // The new authenticator just proved the second factor.
        var gate = await RequireMfaIfNeededAsync(user.Id, appSystem, null, ipAddress, userAgent, ct, enrollment.PrimaryMethod, AuthenticationAssuranceLevel.Mfa);
        if (gate is not null)
            return OperationResult<MfaEnrollmentResult>.Failure(gate.ErrorCode!, gate.Message!);
        await _userManager.RecordSignInAsync(_db, user, _dateTimeProvider.UtcNow, ct);
        await _auditService.LogAsync("LOGIN_SUCCESS", user.Id, appSystem.Code, null, null, ipAddress, userAgent, new { mfa = true, enrolled = true }, ct);
        var session = await BuildAuthResponseAsync(user, appSystem.Id, appSystem.Code, ipAddress, userAgent, null, AuthenticationContext.WithSecondFactor(enrollment.PrimaryMethod), ct);
        return session.IsSuccess
            ? OperationResult<MfaEnrollmentResult>.Success(new MfaEnrollmentResult(session.Data!, enabled.Data!.Codes))
            : OperationResult<MfaEnrollmentResult>.Failure(session.ErrorCode!, session.Message!);
    }

    public async Task<Guid?> GetPasskeyEnrollmentUserAsync(string enrollmentToken, CancellationToken ct = default) =>
        (await ReadEnrollmentAsync(enrollmentToken, EnrollmentKinds.Passkey, ct))?.UserId;

    public async Task CompletePasskeyEnrollmentAsync(string enrollmentToken, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var enrollment = await ReadEnrollmentAsync(enrollmentToken, EnrollmentKinds.Passkey, ct);
        if (enrollment is null || await _transientState.TakeAsync(EnrollmentPurpose, enrollmentToken, ct) is null)
            return;
        var user = await _userManager.FindByIdAsync(enrollment.UserId.ToString());
        await _auditService.LogAsync("PASSKEY_ENROLLED_AT_SIGN_IN", enrollment.UserId, enrollment.ApplicationCode, null, null, ipAddress, userAgent, ct: ct);
        if (user?.Email is not null)
            await _emailService.SendSecurityNoticeAsync(user.Email, user.FullName, "Passkey added", "A passkey was registered for your account while signing in.", ct);
    }

    /// <summary>
    /// A single-use enrollment for a user who proved the first factor but has no factor the
    /// application requires. It expires with the MFA window and is only usable for that factor.
    /// </summary>
    private async Task<string> CreateEnrollmentAsync(Guid userId, string applicationCode, string primaryMethod, string kind, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await _transientState.SetAsync(
            EnrollmentPurpose,
            token,
            System.Text.Json.JsonSerializer.Serialize(new PendingEnrollment(userId, applicationCode, primaryMethod, kind)),
            _dateTimeProvider.UtcNow.AddSeconds(_mfaSettings.MfaTokenExpirySeconds * 2),
            ct);
        return token;
    }

    private async Task<PendingEnrollment?> ReadEnrollmentAsync(string token, string kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
            return null;
        var json = await _transientState.GetAsync(EnrollmentPurpose, token, ct);
        if (json is null)
            return null;
        try
        {
            var enrollment = System.Text.Json.JsonSerializer.Deserialize<PendingEnrollment>(json);
            return enrollment?.Kind == kind ? enrollment : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private const string EnrollmentPurpose = "sign_in_enrollment";

    private static class EnrollmentKinds
    {
        public const string Totp = "totp";
        public const string Passkey = "passkey";
    }

    private sealed record PendingEnrollment(Guid UserId, string ApplicationCode, string PrimaryMethod, string Kind);

    private async Task<OperationResult<AuthResponse>> BuildAuthResponseAsync(ApplicationUser user, Guid appSystemId, string appCode, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        return await BuildAuthResponseAsync(user, appSystemId, appCode, ipAddress, userAgent, null, AuthenticationContext.Password, ct);
    }

    private async Task<OperationResult<AuthResponse>> BuildAuthResponseAsync(ApplicationUser user, Guid appSystemId, string appCode, string? ipAddress, string? userAgent, string? deviceToken, AuthenticationContext authentication, CancellationToken ct)
    {
        return await _sessionIssuer.IssueAsync(user, appSystemId, appCode, ipAddress, userAgent, deviceToken, authentication, ct);
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

    private const string MfaAttemptsPurpose = "mfa_attempts";
    private const int MaximumMfaAttempts = 5;

    private static class SingleUsePurposes
    {
        public const string Mfa = "mfa";
        public const string ForcedChange = "forced_change";
        public const string MagicLink = "magic_link";
    }

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
