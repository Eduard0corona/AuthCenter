using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public sealed class ExternalIdentityLinkService : IExternalIdentityLinkService
{
    private readonly AuthCenterDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IGoogleAuthService _google;
    private readonly IMicrosoftAuthService _microsoft;
    private readonly IGitHubAuthService _gitHub;
    private readonly IAppleAuthService _apple;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public ExternalIdentityLinkService(
        AuthCenterDbContext db,
        UserManager<ApplicationUser> userManager,
        IGoogleAuthService google,
        IMicrosoftAuthService microsoft,
        IGitHubAuthService gitHub,
        IAppleAuthService apple,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _db = db;
        _userManager = userManager;
        _google = google;
        _microsoft = microsoft;
        _gitHub = gitHub;
        _apple = apple;
        _clock = clock;
        _audit = audit;
    }

    public async Task<OperationResult> LinkAsync(
        Guid userId,
        LinkExternalProviderRequest request,
        CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult.Failure("USER_INACTIVE", "User account is inactive.");

        var providerKey = request.Provider.Trim().ToLowerInvariant();
        var provider = providerKey switch
        {
            "google" => DomainConstants.Providers.Google,
            "microsoft" => DomainConstants.Providers.Microsoft,
            "github" => DomainConstants.Providers.GitHub,
            "apple" => DomainConstants.Providers.Apple,
            _ => request.Provider.Trim()
        };
        var payload = await ValidateAsync(providerKey, request.Credential, ct);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Email))
            return OperationResult.Failure("INVALID_EXTERNAL_CREDENTIAL", "External provider credential is invalid.");

        if (!string.Equals(payload.Email, user.Email, StringComparison.OrdinalIgnoreCase))
            return OperationResult.Failure("EXTERNAL_EMAIL_MISMATCH", "External provider email must match the current account.");

        var existingIdentity = await _db.ExternalIdentityProviders
            .FirstOrDefaultAsync(
                identity => identity.Provider == provider && identity.ProviderUserId == payload.Subject,
                ct);

        if (existingIdentity is not null && existingIdentity.UserId != userId)
            return OperationResult.Failure("EXTERNAL_IDENTITY_IN_USE", "External identity is already linked to another account.");

        var now = _clock.UtcNow;
        if (existingIdentity is null)
        {
            _db.ExternalIdentityProviders.Add(new ExternalIdentityProvider
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Provider = provider,
                ProviderUserId = payload.Subject,
                Email = payload.Email,
                DisplayName = payload.Name,
                PictureUrl = payload.PictureUrl,
                LinkedAt = now,
                LastUsedAt = now,
                IsActive = true
            });
        }
        else
        {
            existingIdentity.IsActive = true;
            existingIdentity.Email = payload.Email;
            existingIdentity.DisplayName = payload.Name;
            existingIdentity.PictureUrl = payload.PictureUrl;
            existingIdentity.LastUsedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("LINK_PROVIDER", userId: userId, entityId: provider, ct: ct);
        return OperationResult.Success();
    }

    private async Task<ExternalTokenPayload?> ValidateAsync(
        string provider,
        string credential,
        CancellationToken ct)
    {
        if (provider == DomainConstants.Providers.Google.ToLowerInvariant())
        {
            var googlePayload = await _google.ValidateIdTokenAsync(credential, ct);
            return googlePayload is null
                ? null
                : new ExternalTokenPayload
                {
                    Subject = googlePayload.Subject,
                    Email = googlePayload.Email,
                    Name = googlePayload.Name,
                    PictureUrl = googlePayload.PictureUrl
                };
        }

        if (provider == DomainConstants.Providers.Microsoft.ToLowerInvariant())
            return await _microsoft.ValidateIdTokenAsync(credential, ct);
        if (provider == DomainConstants.Providers.GitHub.ToLowerInvariant())
            return await _gitHub.GetUserFromAccessTokenAsync(credential, ct);
        if (provider == DomainConstants.Providers.Apple.ToLowerInvariant())
            return await _apple.ValidateIdTokenAsync(credential, ct);

        return null;
    }
}
