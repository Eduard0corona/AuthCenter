using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services;

public sealed class ReauthenticationService : IReauthenticationService
{
    private const string CeremonyPurpose = "passkey_step_up";
    private const string ProofPurpose = "reauthentication_proof";
    private static readonly HashSet<string> AllowedPurposes = new(StringComparer.Ordinal)
    {
        "account.change-email", "account.change-password", "account.delete",
        "factor.enroll", "factor.change", "passkey.manage", "session.revoke-all",
        "admin.mfa.reset", "admin.user.delete"
    };

    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly ITransientStateStore _state;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;
    private readonly PasskeySettings _settings;

    public ReauthenticationService(
        UserManager<ApplicationUser> users,
        SignInManager<ApplicationUser> signIn,
        ITransientStateStore state,
        IAuditService audit,
        IDateTimeProvider clock,
        IOptions<PasskeySettings> settings)
    {
        _users = users;
        _signIn = signIn;
        _state = state;
        _audit = audit;
        _clock = clock;
        _settings = settings.Value;
    }

    public async Task<OperationResult<ReauthenticationProofResponse>> VerifyPasswordAsync(
        Guid userId,
        PasswordReauthenticationRequest request,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        if (!IsPurposeAllowed(request.Purpose) || string.IsNullOrWhiteSpace(request.Password))
            return OperationResult<ReauthenticationProofResponse>.Failure("INVALID_REAUTHENTICATION", "A supported purpose and password are required.");

        var user = await _users.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || !user.HasLocalPassword || !await _users.CheckPasswordAsync(user, request.Password))
        {
            await _audit.LogAsync("REAUTHENTICATION_FAILED", userId, ipAddress: ipAddress, userAgent: userAgent, metadata: new { method = "password", request.Purpose }, ct: ct);
            return OperationResult<ReauthenticationProofResponse>.Failure("INVALID_REAUTHENTICATION", "Reauthentication failed.");
        }

        return OperationResult<ReauthenticationProofResponse>.Success(await CreateProofAsync(userId, request.Purpose, AuthenticationAssuranceLevel.Password, ipAddress, userAgent, ct));
    }

    public async Task<OperationResult<PasskeyOptionsResponse>> GetPasskeyOptionsAsync(
        Guid userId,
        BeginPasskeyStepUpRequest request,
        CancellationToken ct = default)
    {
        if (!IsPurposeAllowed(request.Purpose))
            return OperationResult<PasskeyOptionsResponse>.Failure("INVALID_REAUTHENTICATION_PURPOSE", "The reauthentication purpose is not supported.");
        var user = await _users.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || (await _users.GetPasskeysAsync(user)).Count == 0)
            return OperationResult<PasskeyOptionsResponse>.Failure("PASSKEY_NOT_ENROLLED", "No passkey is enrolled for this account.");

        var json = await _signIn.MakePasskeyRequestOptionsAsync(user);
        var interactionId = Guid.NewGuid().ToString("N");
        await _state.SetAsync(CeremonyPurpose, interactionId, $"{userId:N}|{request.Purpose}", _clock.UtcNow.AddMinutes(_settings.CeremonyMinutes), ct);
        return OperationResult<PasskeyOptionsResponse>.Success(new PasskeyOptionsResponse
        {
            InteractionId = interactionId,
            PublicKey = JsonSerializer.Deserialize<JsonElement>(json)
        });
    }

    public async Task<OperationResult<ReauthenticationProofResponse>> VerifyPasskeyAsync(
        Guid userId,
        CompletePasskeyStepUpRequest request,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        if (!IsPurposeAllowed(request.Purpose) || string.IsNullOrWhiteSpace(request.InteractionId))
            return OperationResult<ReauthenticationProofResponse>.Failure("INVALID_REAUTHENTICATION", "A valid ceremony and purpose are required.");
        var expected = await _state.TakeAsync(CeremonyPurpose, request.InteractionId, ct);
        if (!string.Equals(expected, $"{userId:N}|{request.Purpose}", StringComparison.Ordinal))
            return OperationResult<ReauthenticationProofResponse>.Failure("PASSKEY_CEREMONY_EXPIRED", "The passkey ceremony is invalid, expired, or already used.");

        PasskeyAssertionResult<ApplicationUser> assertion;
        try
        {
            assertion = await _signIn.PerformPasskeyAssertionAsync(request.CredentialJson);
        }
        catch (Exception exception) when (exception is PasskeyException or JsonException or FormatException)
        {
            await _audit.LogAsync("REAUTHENTICATION_FAILED", userId, ipAddress: ipAddress, userAgent: userAgent, metadata: new { method = "passkey", request.Purpose }, ct: ct);
            return OperationResult<ReauthenticationProofResponse>.Failure("INVALID_PASSKEY_ASSERTION", "Passkey assertion is invalid or expired.");
        }

        if (!assertion.Succeeded || assertion.User?.Id != userId || assertion.Passkey is null || !assertion.Passkey.IsUserVerified)
            return OperationResult<ReauthenticationProofResponse>.Failure("INVALID_PASSKEY_ASSERTION", "A user-verified passkey is required.");

        var updated = await _users.AddOrUpdatePasskeyAsync(assertion.User, assertion.Passkey);
        if (!updated.Succeeded)
            return OperationResult<ReauthenticationProofResponse>.Failure("PASSKEY_UPDATE_FAILED", "Passkey state could not be updated.");

        return OperationResult<ReauthenticationProofResponse>.Success(await CreateProofAsync(userId, request.Purpose, AuthenticationAssuranceLevel.PhishingResistant, ipAddress, userAgent, ct));
    }

    public async Task<bool> ConsumeProofAsync(Guid userId, string purpose, string? proofToken, CancellationToken ct = default)
    {
        if (!IsPurposeAllowed(purpose) || string.IsNullOrWhiteSpace(proofToken))
            return false;
        var value = await _state.TakeAsync(ProofPurpose, Hash(proofToken), ct);
        return string.Equals(value, $"{userId:N}|{purpose}", StringComparison.Ordinal);
    }

    private async Task<ReauthenticationProofResponse> CreateProofAsync(
        Guid userId,
        string purpose,
        AuthenticationAssuranceLevel assurance,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await _state.SetAsync(ProofPurpose, Hash(token), $"{userId:N}|{purpose}", _clock.UtcNow.AddMinutes(_settings.ReauthenticationMinutes), ct);
        await _audit.LogAsync("REAUTHENTICATION_SUCCESS", userId, ipAddress: ipAddress, userAgent: userAgent, metadata: new { method = assurance.ToString(), purpose }, ct: ct);
        return new ReauthenticationProofResponse
        {
            ProofToken = token,
            AssuranceLevel = assurance.ToString(),
            ExpiresIn = _settings.ReauthenticationMinutes * 60
        };
    }

    private static bool IsPurposeAllowed(string? purpose) => purpose is not null && AllowedPurposes.Contains(purpose);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
