using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public class AccountManagementService : IAccountManagementService
{
    private readonly AuthCenterDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IEmailService _emailService;
    private readonly IActionLinkService _actionLinkService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IAuditService _auditService;
    private readonly ISingleSignOnSessionService _sessions;

    public AccountManagementService(
        AuthCenterDbContext db,
        UserManager<ApplicationUser> userManager,
        IRefreshTokenService refreshTokenService,
        IEmailService emailService,
        IActionLinkService actionLinkService,
        IDateTimeProvider dateTimeProvider,
        IAuditService auditService,
        ISingleSignOnSessionService sessions)
    {
        _db = db;
        _userManager = userManager;
        _refreshTokenService = refreshTokenService;
        _emailService = emailService;
        _actionLinkService = actionLinkService;
        _dateTimeProvider = dateTimeProvider;
        _auditService = auditService;
        _sessions = sessions;
    }

    public async Task<OperationResult> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");

        if (!user.HasLocalPassword)
            return OperationResult.Failure("NO_LOCAL_PASSWORD", "Account uses external login; password cannot be changed.");

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return OperationResult.Failure("PASSWORD_CHANGE_FAILED", string.Join(", ", result.Errors.Select(e => e.Description)));

        user.MustChangePassword = false;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);

        await _auditService.LogAsync("CHANGE_PASSWORD", userId: userId, ct: ct);
        await _emailService.SendSecurityNoticeAsync(user.Email!, user.FullName, "Password changed",
            "The password of your account was changed. If this was not you, reset it now and review your sessions.", ct);
        return OperationResult.Success();
    }

    public async Task<IReadOnlyList<SessionDto>> GetActiveSessionsAsync(Guid userId, CancellationToken ct = default)
    {
        var tokens = await _refreshTokenService.GetActiveSessionsAsync(userId, ct);
        return tokens.Select(t => new SessionDto
        {
            Id = t.Id,
            ApplicationCode = t.ApplicationCode,
            IpAddress = t.IpAddress,
            UserAgent = t.UserAgent,
            CreatedAt = t.CreatedAt,
            ExpiresAt = t.ExpiresAt
        }).ToList();
    }

    public async Task<OperationResult> RevokeSessionAsync(Guid userId, Guid tokenId, CancellationToken ct = default)
    {
        var token = await _refreshTokenService.FindByIdAsync(tokenId, ct);
        if (token is null || token.UserId != userId)
            return OperationResult.Failure("SESSION_NOT_FOUND", "Session not found.");

        if (token.RevokedAt is not null)
            return OperationResult.Failure("SESSION_ALREADY_REVOKED", "Session is already revoked.");

        // A hosted-login session also ends the application grants it authorized and notifies the
        // clients signed in through it; an application grant is revoked on its own.
        if (token.OAuthClientId is null)
        {
            var ended = await _sessions.EndSessionAsync(userId, tokenId, "user_revoked", ct);
            if (!ended.IsSuccess)
                return ended;
        }
        else
        {
            await _refreshTokenService.RevokeAsync(token, null, ct);
        }
        await _auditService.LogAsync("REVOKE_SESSION", userId: userId, entityId: tokenId.ToString(), ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RevokeAllSessionsAsync(Guid userId, CancellationToken ct = default)
    {
        await _refreshTokenService.RevokeAllForUserAsync(userId, ct);
        await _auditService.LogAsync("REVOKE_ALL_SESSIONS", userId: userId, ct: ct);
        return OperationResult.Success();
    }

    public async Task<IReadOnlyList<ExternalProviderDto>> GetExternalProvidersAsync(Guid userId, CancellationToken ct = default)
    {
        var providers = await _db.ExternalIdentityProviders
            .Where(p => p.UserId == userId && p.IsActive)
            .AsNoTracking()
            .ToListAsync(ct);
        // Enterprise links are stored as "Federation:{provider id}"; show the provider's name.
        var enterpriseIds = providers.Select(p => EnterpriseProviderId(p.Provider)).OfType<Guid>().ToList();
        var enterpriseNames = enterpriseIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.FederationProviders.AsNoTracking().Where(p => enterpriseIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        return providers.Select(p =>
        {
            var enterpriseId = EnterpriseProviderId(p.Provider);
            return new ExternalProviderDto
            {
                Id = p.Id,
                Provider = p.Provider,
                ProviderName = enterpriseId is { } id ? enterpriseNames.GetValueOrDefault(id, "Enterprise provider") : p.Provider,
                Enterprise = enterpriseId is not null,
                Email = p.Email,
                DisplayName = p.DisplayName,
                PictureUrl = p.PictureUrl,
                LinkedAt = p.LinkedAt,
                LastUsedAt = p.LastUsedAt
            };
        }).ToList();
    }

    private static Guid? EnterpriseProviderId(string provider) =>
        provider.StartsWith("Federation:", StringComparison.Ordinal) && Guid.TryParseExact(provider["Federation:".Length..], "N", out var id) ? id : null;

    public async Task<IReadOnlyList<UserApplicationDto>> GetApplicationsAsync(Guid userId, CancellationToken ct = default)
    {
        var direct = await _db.UserApplicationAccesses.AsNoTracking()
            .Where(access => access.UserId == userId && access.IsActive && access.ApplicationSystem.IsActive)
            .Select(access => new { access.ApplicationSystemId, access.CreatedAt })
            .ToListAsync(ct);
        var viaGroups = await _db.UserGroupMemberships.AsNoTracking()
            .Where(membership => membership.UserId == userId && membership.Group.IsActive)
            .SelectMany(membership => membership.Group.ApplicationAssignments
                .Where(assignment => assignment.ApplicationSystem.IsActive)
                .Select(assignment => new { assignment.ApplicationSystemId, Group = membership.Group.Name }))
            .ToListAsync(ct);
        var ids = direct.Select(item => item.ApplicationSystemId).Concat(viaGroups.Select(item => item.ApplicationSystemId)).Distinct().ToList();
        if (ids.Count == 0)
            return [];
        var applications = await _db.ApplicationSystems.AsNoTracking()
            .Where(application => ids.Contains(application.Id))
            .Select(application => new
            {
                application.Id, application.Code, application.Name, application.Description,
                DisplayName = application.BrandingSettings != null ? application.BrandingSettings.DisplayName : null,
                LogoUrl = application.BrandingSettings != null ? application.BrandingSettings.LogoUrl : null,
                SupportUrl = application.BrandingSettings != null ? application.BrandingSettings.SupportUrl : null
            })
            .ToListAsync(ct);
        return applications
            .Select(application => new UserApplicationDto
            {
                Code = application.Code,
                Name = string.IsNullOrWhiteSpace(application.DisplayName) ? application.Name : application.DisplayName,
                Description = application.Description,
                LogoUrl = application.LogoUrl,
                SupportUrl = application.SupportUrl,
                GrantedAt = direct.Where(item => item.ApplicationSystemId == application.Id).Select(item => (DateTime?)item.CreatedAt).Min(),
                Groups = viaGroups.Where(item => item.ApplicationSystemId == application.Id).Select(item => item.Group).Distinct().Order().ToList()
            })
            .OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<OperationResult> UnlinkExternalProviderAsync(Guid userId, Guid providerId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");

        var provider = await _db.ExternalIdentityProviders
            .FirstOrDefaultAsync(p => p.Id == providerId && p.UserId == userId && p.IsActive, ct);

        if (provider is null)
            return OperationResult.Failure("PROVIDER_NOT_FOUND", "External provider not found.");

        var activeProviderCount = await _db.ExternalIdentityProviders
            .CountAsync(p => p.UserId == userId && p.IsActive, ct);

        if (!user.HasLocalPassword && activeProviderCount <= 1)
            return OperationResult.Failure("CANNOT_UNLINK_LAST_PROVIDER", "Cannot unlink the only login method. Set a password first.");

        provider.IsActive = false;
        await _db.SaveChangesAsync(ct);

        await _auditService.LogAsync("UNLINK_PROVIDER", userId: userId, entityId: provider.Provider, ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RequestEmailChangeAsync(Guid userId, RequestEmailChangeRequest request, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");

        var newEmail = request.NewEmail.Trim().ToLowerInvariant();

        if (string.Equals(user.Email, newEmail, StringComparison.OrdinalIgnoreCase))
            return OperationResult.Failure("SAME_EMAIL", "New email is the same as the current email.");

        var existing = await _userManager.FindByEmailAsync(newEmail);
        if (existing is not null)
            return OperationResult.Failure("EMAIL_TAKEN", "This email is already in use.");

        var token = await _userManager.GenerateChangeEmailTokenAsync(user, newEmail);
        // The confirmation needs the account as well as the new address and token, and the
        // link may be opened in a browser that is not signed in.
        var actionUrl = QueryHelpers.AddQueryString(_actionLinkService.GetActionUrl(ActionLinkPurpose.EmailChange), "userId", user.Id.ToString());
        await _emailService.SendEmailChangeConfirmationAsync(newEmail, user.FullName, token, actionUrl, ct);
        await _emailService.SendSecurityNoticeAsync(user.Email!, user.FullName, "Email change requested",
            "A change of your account's email address was requested. It only applies once confirmed from the new address; if this was not you, change your password.", ct);

        await _auditService.LogAsync("REQUEST_EMAIL_CHANGE", userId: userId, ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ConfirmEmailChangeAsync(ConfirmEmailChangeRequest request, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");

        var previousEmail = user.Email;
        var result = await _userManager.ChangeEmailAsync(user, request.NewEmail, request.Token);
        if (!result.Succeeded)
            return OperationResult.Failure("EMAIL_CHANGE_FAILED", "The confirmation link is invalid or expired.");

        // Keep UserName in sync with Email
        user.UserName = request.NewEmail;
        user.UpdatedAt = _dateTimeProvider.UtcNow;
        await _userManager.UpdateAsync(user);

        await _auditService.LogAsync("CONFIRM_EMAIL_CHANGE", userId: user.Id, ct: ct);
        if (!string.IsNullOrWhiteSpace(previousEmail))
            await _emailService.SendSecurityNoticeAsync(previousEmail, user.FullName, "Email address changed",
                "Your account now uses a different email address. If this was not you, contact support right away.", ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeleteAccountAsync(Guid userId, DeleteAccountRequest request, CancellationToken ct = default)
    {
        if (!request.ConfirmDeletion)
            return OperationResult.Failure("DELETION_NOT_CONFIRMED", "Account deletion must be explicitly confirmed.");

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");

        if (user.HasLocalPassword)
        {
            if (string.IsNullOrWhiteSpace(request.Password))
                return OperationResult.Failure("PASSWORD_REQUIRED", "Password is required to delete an account with local login.");

            var passwordValid = await _userManager.CheckPasswordAsync(user, request.Password);
            if (!passwordValid)
                return OperationResult.Failure("INVALID_PASSWORD", "Incorrect password.");
        }

        var now = _dateTimeProvider.UtcNow;

        await _refreshTokenService.RevokeAllForUserAsync(userId, ct);

        user.DeletedAt = now;
        user.IsActive = false;
        user.UpdatedAt = now;
        user.FullName = "Deleted User";
        user.PictureUrl = null;
        user.PhoneNumber = null;

        var anonymizedEmail = $"deleted_{userId:N}@deleted.invalid";
        user.Email = anonymizedEmail;
        user.NormalizedEmail = anonymizedEmail.ToUpperInvariant();
        user.UserName = anonymizedEmail;
        user.NormalizedUserName = anonymizedEmail.ToUpperInvariant();

        await _userManager.UpdateAsync(user);

        await _auditService.LogAsync("DELETE_ACCOUNT", userId: userId, ct: ct);
        return OperationResult.Success();
    }

    public async Task<IReadOnlyList<TrustedDeviceDto>> GetTrustedDevicesAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.UserTrustedDevices
            .AsNoTracking()
            .Where(d => d.UserId == userId && d.ExpiresAt > _dateTimeProvider.UtcNow)
            .OrderByDescending(d => d.LastUsedAt ?? d.CreatedAt)
            .Select(d => new TrustedDeviceDto
            {
                Id = d.Id,
                DeviceName = d.DeviceName,
                CreatedAt = d.CreatedAt,
                ExpiresAt = d.ExpiresAt,
                LastUsedAt = d.LastUsedAt
            })
            .ToListAsync(ct);
    }

    public async Task<OperationResult> RevokeTrustedDeviceAsync(Guid userId, Guid deviceId, CancellationToken ct = default)
    {
        var device = await _db.UserTrustedDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId, ct);
        if (device is null)
            return OperationResult.Failure("TRUSTED_DEVICE_NOT_FOUND", "Trusted device not found.");

        _db.UserTrustedDevices.Remove(device);
        await _db.SaveChangesAsync(ct);
        await _auditService.LogAsync("TRUSTED_DEVICE_REVOKED", userId: userId, entityId: deviceId.ToString(), ct: ct);

        return OperationResult.Success();
    }

    public async Task<OperationResult> RevokeAllTrustedDevicesAsync(Guid userId, CancellationToken ct = default)
    {
        var devices = await _db.UserTrustedDevices.Where(d => d.UserId == userId).ToListAsync(ct);
        if (devices.Count == 0)
            return OperationResult.Success();

        _db.UserTrustedDevices.RemoveRange(devices);
        await _db.SaveChangesAsync(ct);
        await _auditService.LogAsync("TRUSTED_DEVICES_REVOKED", userId: userId, ct: ct);

        return OperationResult.Success();
    }
}
