using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services;

public sealed class PasskeyService : IPasskeyService
{
    private const string LoginStatePurpose = "passkey_login";
    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly AuthCenterDbContext _db;
    private readonly ITransientStateStore _state;
    private readonly IUserAccessService _access;
    private readonly IAccessPolicyService _policies;
    private readonly IAuthenticationSessionIssuer _sessions;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;
    private readonly PasskeySettings _settings;
    private readonly IAuthenticationRiskService _authenticationRisk;

    public PasskeyService(
        UserManager<ApplicationUser> users,
        SignInManager<ApplicationUser> signIn,
        AuthCenterDbContext db,
        ITransientStateStore state,
        IUserAccessService access,
        IAccessPolicyService policies,
        IAuthenticationSessionIssuer sessions,
        IAuditService audit,
        IDateTimeProvider clock,
        IOptions<PasskeySettings> settings,
        IAuthenticationRiskService authenticationRisk)
    {
        _users = users;
        _signIn = signIn;
        _db = db;
        _state = state;
        _access = access;
        _policies = policies;
        _sessions = sessions;
        _audit = audit;
        _clock = clock;
        _settings = settings.Value;
        _authenticationRisk = authenticationRisk;
    }

    public async Task<OperationResult<PasskeyOptionsResponse>> GetRegistrationOptionsAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive)
            return OperationResult<PasskeyOptionsResponse>.Failure("USER_NOT_FOUND", "Active user not found.");
        if ((await _users.GetPasskeysAsync(user)).Count >= _settings.MaxCredentialsPerUser)
            return OperationResult<PasskeyOptionsResponse>.Failure("PASSKEY_LIMIT_REACHED", "The maximum number of passkeys has been reached.");

        var json = await _signIn.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
        {
            Id = user.Id.ToString(),
            Name = user.Email ?? user.UserName ?? user.Id.ToString(),
            DisplayName = user.FullName
        });
        return OperationResult<PasskeyOptionsResponse>.Success(new PasskeyOptionsResponse
        {
            PublicKey = JsonSerializer.Deserialize<JsonElement>(json)
        });
    }

    public async Task<OperationResult<PasskeyCredentialDto>> RegisterAsync(
        Guid userId,
        RegisterPasskeyRequest request,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        if (!IsValidName(request.Name) || !LooksLikeCredentialJson(request.CredentialJson))
            return OperationResult<PasskeyCredentialDto>.Failure("INVALID_PASSKEY", "Credential JSON and a name of at most 100 characters are required.");
        var user = await _users.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive)
            return OperationResult<PasskeyCredentialDto>.Failure("USER_NOT_FOUND", "Active user not found.");
        if ((await _users.GetPasskeysAsync(user)).Count >= _settings.MaxCredentialsPerUser)
            return OperationResult<PasskeyCredentialDto>.Failure("PASSKEY_LIMIT_REACHED", "The maximum number of passkeys has been reached.");

        PasskeyAttestationResult result;
        try
        {
            result = await _signIn.PerformPasskeyAttestationAsync(request.CredentialJson);
        }
        catch (Exception exception) when (exception is PasskeyException or JsonException or FormatException)
        {
            await _audit.LogAsync("PASSKEY_REGISTRATION_FAILED", userId, ipAddress: ipAddress, userAgent: userAgent, ct: ct);
            return OperationResult<PasskeyCredentialDto>.Failure("INVALID_PASSKEY_ATTESTATION", "Passkey attestation is invalid, expired, or belongs to another ceremony.");
        }
        if (!result.Succeeded || result.Passkey is null || result.UserEntity?.Id != user.Id.ToString())
        {
            await _audit.LogAsync("PASSKEY_REGISTRATION_FAILED", userId, ipAddress: ipAddress, userAgent: userAgent, ct: ct);
            return OperationResult<PasskeyCredentialDto>.Failure("INVALID_PASSKEY_ATTESTATION", "Passkey attestation is invalid, expired, or belongs to another ceremony.");
        }

        result.Passkey.Name = request.Name.Trim();
        var stored = await _users.AddOrUpdatePasskeyAsync(user, result.Passkey);
        if (!stored.Succeeded)
            return OperationResult<PasskeyCredentialDto>.Failure("PASSKEY_STORAGE_FAILED", "The passkey could not be stored.");

        await _audit.LogAsync("PASSKEY_REGISTERED", userId, entityName: "Passkey", entityId: Encode(result.Passkey.CredentialId), ipAddress: ipAddress, userAgent: userAgent, ct: ct);
        return OperationResult<PasskeyCredentialDto>.Success(Map(result.Passkey));
    }

    public async Task<IReadOnlyList<PasskeyCredentialDto>> GetAllAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId.ToString());
        return user is null ? [] : (await _users.GetPasskeysAsync(user)).Select(Map).OrderBy(item => item.Name).ToList();
    }

    public async Task<OperationResult> RenameAsync(Guid userId, string credentialId, RenamePasskeyRequest request, CancellationToken ct = default)
    {
        if (!IsValidName(request.Name) || !TryDecode(credentialId, out var id))
            return OperationResult.Failure("INVALID_PASSKEY", "A valid credential ID and name are required.");
        var user = await _users.FindByIdAsync(userId.ToString());
        var passkey = user is null ? null : await _users.GetPasskeyAsync(user, id);
        if (user is null || passkey is null)
            return OperationResult.Failure("PASSKEY_NOT_FOUND", "Passkey not found.");
        passkey.Name = request.Name.Trim();
        var result = await _users.AddOrUpdatePasskeyAsync(user, passkey);
        if (!result.Succeeded)
            return OperationResult.Failure("PASSKEY_UPDATE_FAILED", "The passkey could not be updated.");
        await _audit.LogAsync("PASSKEY_RENAMED", userId, entityName: "Passkey", entityId: credentialId, ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveAsync(Guid userId, string credentialId, CancellationToken ct = default)
    {
        if (!TryDecode(credentialId, out var id))
            return OperationResult.Failure("INVALID_PASSKEY_ID", "Credential ID is invalid.");
        var user = await _users.FindByIdAsync(userId.ToString());
        var passkey = user is null ? null : await _users.GetPasskeyAsync(user, id);
        if (user is null || passkey is null)
            return OperationResult.Failure("PASSKEY_NOT_FOUND", "Passkey not found.");
        var result = await _users.RemovePasskeyAsync(user, id);
        if (!result.Succeeded)
            return OperationResult.Failure("PASSKEY_REMOVE_FAILED", "The passkey could not be removed.");
        await _audit.LogAsync("PASSKEY_REMOVED", userId, entityName: "Passkey", entityId: credentialId, ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult<PasskeyOptionsResponse>> GetLoginOptionsAsync(BeginPasskeyLoginRequest request, CancellationToken ct = default)
    {
        var app = await _db.ApplicationSystems.AsNoTracking().FirstOrDefaultAsync(item => item.Code == request.ApplicationCode && item.IsActive, ct);
        if (app is null)
            return OperationResult<PasskeyOptionsResponse>.Failure("APP_NOT_FOUND", "Application not found or inactive.");
        ApplicationUser? user = null;
        if (!string.IsNullOrWhiteSpace(request.Email))
            user = await _users.FindByEmailAsync(request.Email.Trim());
        var json = await _signIn.MakePasskeyRequestOptionsAsync(user);
        var interactionId = Guid.NewGuid().ToString("N");
        await _state.SetAsync(LoginStatePurpose, interactionId, app.Id.ToString(), _clock.UtcNow.AddMinutes(_settings.CeremonyMinutes), ct);
        return OperationResult<PasskeyOptionsResponse>.Success(new PasskeyOptionsResponse
        {
            InteractionId = interactionId,
            PublicKey = JsonSerializer.Deserialize<JsonElement>(json)
        });
    }

    public async Task<OperationResult<AuthResponse>> LoginAsync(
        CompletePasskeyLoginRequest request,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.InteractionId))
            return OperationResult<AuthResponse>.Failure("INVALID_PASSKEY_REQUEST", "Interaction and credential are required.");
        var appIdValue = await _state.TakeAsync(LoginStatePurpose, request.InteractionId, ct);
        if (!Guid.TryParse(appIdValue, out var appId))
            return OperationResult<AuthResponse>.Failure("PASSKEY_CEREMONY_EXPIRED", "The passkey ceremony is invalid, expired, or already used.");
        if (!LooksLikeCredentialJson(request.CredentialJson))
            return OperationResult<AuthResponse>.Failure("INVALID_PASSKEY_ASSERTION", "Passkey assertion is invalid or expired.");

        PasskeyAssertionResult<ApplicationUser> assertion;
        try
        {
            assertion = await _signIn.PerformPasskeyAssertionAsync(request.CredentialJson);
        }
        catch (Exception exception) when (exception is PasskeyException or JsonException or FormatException)
        {
            await _audit.LogAsync("PASSKEY_LOGIN_FAILED", null, ipAddress: ipAddress, userAgent: userAgent, ct: ct);
            return OperationResult<AuthResponse>.Failure("INVALID_PASSKEY_ASSERTION", "Passkey assertion is invalid or expired.");
        }
        if (!assertion.Succeeded || assertion.User is null || assertion.Passkey is null || !assertion.Passkey.IsUserVerified)
        {
            await _audit.LogAsync("PASSKEY_LOGIN_FAILED", null, ipAddress: ipAddress, userAgent: userAgent, ct: ct);
            return OperationResult<AuthResponse>.Failure("INVALID_PASSKEY_ASSERTION", "Passkey assertion is invalid or expired.");
        }

        var user = assertion.User;
        var app = await _db.ApplicationSystems.AsNoTracking().FirstOrDefaultAsync(item => item.Id == appId && item.IsActive, ct);
        if (!user.IsActive || app is null || !await _access.HasActiveAccessAsync(user.Id, appId, ct))
            return OperationResult<AuthResponse>.Failure("ACCESS_DENIED", "User or application access is inactive.");
        var signals = await _authenticationRisk.AssessAndRecordAsync(user.Id, ipAddress, userAgent, ct: ct);
        var policy = await _policies.EvaluateAsync(new AuthCenter.Application.Models.AccessPolicyEvaluationContext(
            user.Id,
            appId,
            ipAddress,
            _clock.UtcNow,
            signals.RiskLevel,
            AuthCenter.Domain.Enums.AuthenticationAssuranceLevel.PhishingResistant), ct);
        if (!policy.IsAllowed)
            return OperationResult<AuthResponse>.Failure("ACCESS_POLICY_DENIED", "Sign-in is denied by the application's access policy.");

        var updated = await _users.AddOrUpdatePasskeyAsync(user, assertion.Passkey);
        if (!updated.Succeeded)
            return OperationResult<AuthResponse>.Failure("PASSKEY_UPDATE_FAILED", "Passkey state could not be updated.");
        user.LastLoginAt = _clock.UtcNow;
        user.UpdatedAt = _clock.UtcNow;
        await _users.UpdateAsync(user);
        await _audit.LogAsync("PASSKEY_LOGIN_SUCCESS", user.Id, app.Code, "Passkey", Encode(assertion.Passkey.CredentialId), ipAddress, userAgent, new { phishingResistant = true, userVerified = assertion.Passkey.IsUserVerified }, ct);
        return await _sessions.IssueAsync(user, app.Id, app.Code, ipAddress, userAgent, ct: ct);
    }

    private static PasskeyCredentialDto Map(UserPasskeyInfo passkey) => new()
    {
        CredentialId = Encode(passkey.CredentialId),
        Name = passkey.Name ?? "Passkey",
        CreatedAt = passkey.CreatedAt,
        Transports = passkey.Transports ?? [],
        IsUserVerified = passkey.IsUserVerified,
        IsBackupEligible = passkey.IsBackupEligible,
        IsBackedUp = passkey.IsBackedUp
    };

    private static bool IsValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 100;
    private static bool LooksLikeCredentialJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128_000)
            return false;
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("id", out _) &&
                root.TryGetProperty("rawId", out _) &&
                root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("type", out var type) && type.GetString() == "public-key";
        }
        catch (JsonException)
        {
            return false;
        }
    }
    private static string Encode(byte[] value) => WebEncoders.Base64UrlEncode(value);
    private static bool TryDecode(string value, out byte[] result)
    {
        try { result = WebEncoders.Base64UrlDecode(value); return result.Length > 0; }
        catch (FormatException) { result = []; return false; }
    }
}
