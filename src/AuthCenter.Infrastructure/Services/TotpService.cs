using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OtpNet;

namespace AuthCenter.Infrastructure.Services;

public class TotpService : IMfaService
{
    private readonly AuthCenterDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly MfaSettings _settings;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IAuditService _auditService;
    private readonly IEmailService _emailService;
    private readonly ITransientStateStore _transientState;

    public TotpService(
        AuthCenterDbContext db,
        UserManager<ApplicationUser> userManager,
        IOptions<MfaSettings> settings,
        IDateTimeProvider dateTimeProvider,
        IAuditService auditService,
        IEmailService emailService,
        ITransientStateStore transientState)
    {
        _db = db;
        _userManager = userManager;
        _settings = settings.Value;
        _dateTimeProvider = dateTimeProvider;
        _auditService = auditService;
        _emailService = emailService;
        _transientState = transientState;
    }

    public async Task<OperationResult<MfaSetupResponse>> SetupTotpAsync(Guid userId, CancellationToken ct = default)
    {
        if (!HasEncryptionKey())
            return OperationResult<MfaSetupResponse>.Failure("MFA_NOT_CONFIGURED", "MFA encryption is not configured.");

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult<MfaSetupResponse>.Failure("USER_NOT_FOUND", "User not found.");

        var existing = await _db.UserMfaCredentials.FirstOrDefaultAsync(m => m.UserId == userId, ct);
        if (existing?.IsEnabled == true)
            return OperationResult<MfaSetupResponse>.Failure("MFA_ALREADY_ENABLED", "MFA is already enabled.");

        var secretBytes = KeyGeneration.GenerateRandomKey(20);
        var secretBase32 = Base32Encoding.ToString(secretBytes);
        var now = _dateTimeProvider.UtcNow;

        if (existing is null)
        {
            existing = new UserMfaCredential
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now
            };
            _db.UserMfaCredentials.Add(existing);
        }

        existing.EncryptedTotpSecret = Encrypt(secretBase32);
        existing.Method = MfaMethod.Totp;
        existing.IsEnabled = false;
        existing.EnabledAt = null;
        existing.HashedBackupCodes = null;
        existing.BackupCodesRegeneratedAt = null;
        existing.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        await _auditService.LogAsync("MFA_SETUP_STARTED", userId, ct: ct);

        var issuer = _settings.TotpIssuer;
        var email = user.Email ?? user.UserName ?? user.Id.ToString();
        var totpUri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(email)}?secret={secretBase32}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";

        return OperationResult<MfaSetupResponse>.Success(new MfaSetupResponse
        {
            TotpUri = totpUri,
            SecretBase32 = secretBase32
        });
    }

    public async Task<OperationResult<BackupCodesResponse>> EnableTotpAsync(Guid userId, EnableMfaRequest request, CancellationToken ct = default)
    {
        var credential = await _db.UserMfaCredentials.FirstOrDefaultAsync(m => m.UserId == userId, ct);
        if (credential is null)
            return OperationResult<BackupCodesResponse>.Failure("MFA_NOT_SETUP", "MFA setup has not been started.");

        if (credential.IsEnabled)
            return OperationResult<BackupCodesResponse>.Failure("MFA_ALREADY_ENABLED", "MFA is already enabled.");

        if (!await VerifyTotpOnceAsync(credential, request.TotpCode, ct))
            return OperationResult<BackupCodesResponse>.Failure("INVALID_MFA_CODE", "The MFA code is invalid.");

        var (rawCodes, hashes) = GenerateBackupCodes();
        var now = _dateTimeProvider.UtcNow;
        credential.Method = MfaMethod.Totp;
        credential.IsEnabled = true;
        credential.EnabledAt = now;
        credential.HashedBackupCodes = JsonSerializer.Serialize(hashes);
        credential.BackupCodesRegeneratedAt = now;
        credential.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        await _auditService.LogAsync("MFA_ENABLED", userId, ct: ct);

        return OperationResult<BackupCodesResponse>.Success(new BackupCodesResponse { Codes = rawCodes });
    }

    public async Task<bool> VerifyTotpCodeAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var credential = await _db.UserMfaCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.IsEnabled, ct);

        return credential is not null && await VerifyTotpOnceAsync(credential, code, ct);
    }

    public async Task<bool> UseBackupCodeAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var credential = await _db.UserMfaCredentials.FirstOrDefaultAsync(m => m.UserId == userId && m.IsEnabled, ct);
        if (credential?.HashedBackupCodes is null)
            return false;

        var hashes = DeserializeBackupCodeHashes(credential.HashedBackupCodes);
        var hash = HashBackupCode(code);
        if (!hashes.Remove(hash))
            return false;

        credential.HashedBackupCodes = JsonSerializer.Serialize(hashes);
        credential.UpdatedAt = _dateTimeProvider.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request consumed the same code first.
            return false;
        }

        await _auditService.LogAsync("MFA_BACKUP_CODE_USED", userId, ct: ct);
        if (hashes.Count == 0)
            await _auditService.LogAsync("MFA_BACKUP_CODE_EXHAUSTED", userId, ct: ct);

        return true;
    }

    public async Task<OperationResult> DisableMfaAsync(Guid userId, DisableMfaRequest request, CancellationToken ct = default)
    {
        var credential = await _db.UserMfaCredentials.FirstOrDefaultAsync(m => m.UserId == userId && m.IsEnabled, ct);
        if (credential is null)
            return OperationResult.Failure("MFA_NOT_ENABLED", "MFA is not enabled.");

        var verified = false;
        if (!string.IsNullOrWhiteSpace(request.TotpCode))
            verified = await VerifyTotpOnceAsync(credential, request.TotpCode, ct);

        if (!verified && !string.IsNullOrWhiteSpace(request.BackupCode))
            verified = VerifyBackupCode(credential, request.BackupCode);

        if (!verified && !string.IsNullOrWhiteSpace(request.EmailOtpCode) && credential.Method == MfaMethod.EmailOtp)
        {
            var storedCode = await _transientState.GetAsync(MfaStatePurposes.EmailOtpSetup, userId.ToString(), ct);
            if (storedCode is not null && storedCode == request.EmailOtpCode.Trim())
            {
                await _transientState.RemoveAsync(MfaStatePurposes.EmailOtpSetup, userId.ToString(), ct);
                verified = true;
            }
        }

        if (!verified)
            return OperationResult.Failure("INVALID_MFA_CODE", "The MFA code is invalid.");

        _db.UserMfaCredentials.Remove(credential);
        await _db.SaveChangesAsync(ct);
        await _auditService.LogAsync("MFA_DISABLED", userId, ct: ct);
        if (await _userManager.FindByIdAsync(userId.ToString()) is { Email: not null } user)
            await _emailService.SendSecurityNoticeAsync(user.Email, user.FullName, "Two-step verification disabled",
                "Two-step verification was turned off for your account. If this was not you, change your password and turn it on again.", ct);

        return OperationResult.Success();
    }

    public async Task<OperationResult<BackupCodesResponse>> RegenerateBackupCodesAsync(Guid userId, string totpCode, CancellationToken ct = default)
    {
        var credential = await _db.UserMfaCredentials.FirstOrDefaultAsync(m => m.UserId == userId && m.IsEnabled, ct);
        if (credential is null)
            return OperationResult<BackupCodesResponse>.Failure("MFA_NOT_ENABLED", "MFA is not enabled.");

        if (!await VerifyTotpOnceAsync(credential, totpCode, ct))
            return OperationResult<BackupCodesResponse>.Failure("INVALID_MFA_CODE", "The MFA code is invalid.");

        var (rawCodes, hashes) = GenerateBackupCodes();
        var now = _dateTimeProvider.UtcNow;
        credential.HashedBackupCodes = JsonSerializer.Serialize(hashes);
        credential.BackupCodesRegeneratedAt = now;
        credential.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        await _auditService.LogAsync("MFA_BACKUP_CODES_REGENERATED", userId, ct: ct);

        return OperationResult<BackupCodesResponse>.Success(new BackupCodesResponse { Codes = rawCodes });
    }

    public async Task<MfaStatusDto> GetStatusAsync(Guid userId, CancellationToken ct = default)
    {
        var credential = await _db.UserMfaCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == userId, ct);

        if (credential is null)
            return new MfaStatusDto();

        return new MfaStatusDto
        {
            IsEnabled = credential.IsEnabled,
            Method = credential.Method.ToString(),
            EnabledAt = credential.EnabledAt,
            HasBackupCodes = !string.IsNullOrWhiteSpace(credential.HashedBackupCodes) &&
                DeserializeBackupCodeHashes(credential.HashedBackupCodes).Count > 0,
            BackupCodesRegeneratedAt = credential.BackupCodesRegeneratedAt
        };
    }

    public async Task<OperationResult> AdminResetMfaAsync(Guid userId, CancellationToken ct = default)
    {
        var credential = await _db.UserMfaCredentials.FirstOrDefaultAsync(m => m.UserId == userId, ct);
        if (credential is null)
            return OperationResult.Success();

        _db.UserMfaCredentials.Remove(credential);
        await _db.SaveChangesAsync(ct);
        await _transientState.RemoveAsync(MfaStatePurposes.EmailOtpSetup, userId.ToString(), ct);
        await _auditService.LogAsync("MFA_ADMIN_RESET", userId, ct: ct);

        return OperationResult.Success();
    }

    public async Task<OperationResult> SetupEmailOtpAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");

        var existing = await _db.UserMfaCredentials.FirstOrDefaultAsync(m => m.UserId == userId, ct);
        if (existing?.IsEnabled == true)
            return OperationResult.Failure("MFA_ALREADY_ENABLED", "MFA is already enabled.");

        var code = GenerateNumericCode();
        await _transientState.SetAsync(
            MfaStatePurposes.EmailOtpSetup,
            userId.ToString(),
            code,
            _dateTimeProvider.UtcNow.AddMinutes(10),
            ct);

        await _emailService.SendMfaEmailOtpAsync(user.Email!, user.FullName, code, ct);
        await _auditService.LogAsync("MFA_EMAIL_OTP_SETUP_SENT", userId, ct: ct);

        return OperationResult.Success();
    }

    public async Task<OperationResult> SendEmailOtpVerificationAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return OperationResult.Failure("USER_NOT_FOUND", "User not found.");
        // Only an enabled email factor has no other way (authenticator or backup code) to prove itself.
        if (!await _db.UserMfaCredentials.AnyAsync(m => m.UserId == userId && m.IsEnabled && m.Method == MfaMethod.EmailOtp, ct))
            return OperationResult.Failure("MFA_NOT_ENABLED", "Email MFA is not enabled.");

        var code = GenerateNumericCode();
        await _transientState.SetAsync(MfaStatePurposes.EmailOtpSetup, userId.ToString(), code, _dateTimeProvider.UtcNow.AddMinutes(10), ct);
        await _emailService.SendMfaEmailOtpAsync(user.Email!, user.FullName, code, ct);
        await _auditService.LogAsync("MFA_EMAIL_OTP_VERIFICATION_SENT", userId, ct: ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> EnableEmailOtpAsync(Guid userId, EnableEmailMfaRequest request, CancellationToken ct = default)
    {
        var storedCode = await _transientState.GetAsync(MfaStatePurposes.EmailOtpSetup, userId.ToString(), ct);
        if (storedCode is null)
            return OperationResult.Failure("SETUP_NOT_INITIATED", "No pending email OTP setup. Call /mfa/email-otp/setup first.");

        if (storedCode != request.Code.Trim())
            return OperationResult.Failure("INVALID_CODE", "The verification code is incorrect.");

        await _transientState.RemoveAsync(MfaStatePurposes.EmailOtpSetup, userId.ToString(), ct);

        var existing = await _db.UserMfaCredentials.FirstOrDefaultAsync(m => m.UserId == userId, ct);
        if (existing?.IsEnabled == true)
            return OperationResult.Failure("MFA_ALREADY_ENABLED", "MFA is already enabled.");

        var now = _dateTimeProvider.UtcNow;
        if (existing is null)
        {
            _db.UserMfaCredentials.Add(new UserMfaCredential
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Method = MfaMethod.EmailOtp,
                EncryptedTotpSecret = string.Empty,
                IsEnabled = true,
                EnabledAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            existing.Method = MfaMethod.EmailOtp;
            existing.EncryptedTotpSecret = string.Empty;
            existing.IsEnabled = true;
            existing.EnabledAt = now;
            existing.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        await _auditService.LogAsync("MFA_EMAIL_OTP_ENABLED", userId, ct: ct);

        return OperationResult.Success();
    }

    public async Task<bool> SendMfaEmailOtpAsync(Guid userId, string pendingTokenJti, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || user.DeletedAt is not null)
            return false;

        var credential = await _db.UserMfaCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.IsEnabled && m.Method == MfaMethod.EmailOtp, ct);
        if (credential is null)
            return false;

        var code = GenerateNumericCode();
        await _transientState.SetAsync(
            MfaStatePurposes.EmailOtpVerify,
            pendingTokenJti,
            code,
            _dateTimeProvider.UtcNow.AddSeconds(_settings.MfaTokenExpirySeconds),
            ct);

        await _emailService.SendMfaEmailOtpAsync(user.Email!, user.FullName, code, ct);
        await _auditService.LogAsync("MFA_EMAIL_OTP_SENT", userId, ct: ct);

        return true;
    }

    /// <summary>
    /// A code proves the factor once (RFC 6238 §5.2): the time step it matched is remembered for
    /// longer than the verification window, so an observed or phished code cannot be replayed.
    /// </summary>
    private async Task<bool> VerifyTotpOnceAsync(UserMfaCredential credential, string? code, CancellationToken ct)
    {
        if (!TryMatchTotp(credential, code, out var timeStep))
            return false;
        return await _transientState.TryConsumeAsync(
            MfaStatePurposes.TotpStepUsed,
            $"{credential.UserId:N}:{timeStep}",
            _dateTimeProvider.UtcNow.AddMinutes(5),
            ct);
    }

    private bool TryMatchTotp(UserMfaCredential credential, string? code, out long timeStep)
    {
        timeStep = 0;
        if (!HasEncryptionKey() || string.IsNullOrWhiteSpace(code))
            return false;

        try
        {
            var secretBase32 = Decrypt(credential.EncryptedTotpSecret);
            var secretBytes = Base32Encoding.ToBytes(secretBase32);
            var totp = new Totp(secretBytes);
            return totp.VerifyTotp(
                _dateTimeProvider.UtcNow,
                code.Trim(),
                out timeStep,
                new VerificationWindow(previous: 1, future: 1));
        }
        catch
        {
            return false;
        }
    }

    private string Encrypt(string plaintext)
    {
        var key = GetEncryptionKey(_settings.EncryptionKey);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plaintextBytes.Length];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintextBytes, cipherBytes, tag);

        var payload = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, payload, nonce.Length + tag.Length, cipherBytes.Length);
        return $"v2.{GetKeyId(key)}.{Convert.ToBase64String(payload)}";
    }

    private string Decrypt(string ciphertext)
    {
        var keys = GetEncryptionKeys();
        if (ciphertext.StartsWith("v2.", StringComparison.Ordinal))
        {
            var parts = ciphertext.Split('.', 3);
            if (parts.Length != 3)
                throw new CryptographicException("Invalid encrypted MFA secret format.");

            var key = keys.FirstOrDefault(candidate => GetKeyId(candidate) == parts[1])
                ?? throw new CryptographicException("The MFA secret encryption key is unavailable.");
            var payload = Convert.FromBase64String(parts[2]);
            if (payload.Length < 29)
                throw new CryptographicException("Invalid encrypted MFA secret payload.");

            var nonce = payload[..12];
            var tag = payload[12..28];
            var cipherBytes = payload[28..];
            var plaintext = new byte[cipherBytes.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, cipherBytes, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }

        // Transitional reader for the former AES-CBC format. New writes always use authenticated
        // v2 envelopes, while previous keys allow rolling rotation without locking users out.
        var legacyPayload = Convert.FromBase64String(ciphertext);
        foreach (var key in keys)
        {
            try
            {
                using var aes = Aes.Create();
                aes.Key = key;
                aes.IV = legacyPayload[..16];
                using var decryptor = aes.CreateDecryptor();
                var plaintext = decryptor.TransformFinalBlock(legacyPayload, 16, legacyPayload.Length - 16);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch (CryptographicException)
            {
                // Try the next configured key.
            }
        }

        throw new CryptographicException("Unable to decrypt the MFA secret.");
    }

    private string HashBackupCode(string code) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToLowerInvariant())));

    private bool VerifyBackupCode(UserMfaCredential credential, string code)
    {
        if (string.IsNullOrWhiteSpace(credential.HashedBackupCodes))
            return false;

        var hashes = DeserializeBackupCodeHashes(credential.HashedBackupCodes);
        return hashes.Contains(HashBackupCode(code));
    }

    private (List<string> rawCodes, List<string> hashes) GenerateBackupCodes(int count = 8)
    {
        var rawCodes = new List<string>(count);
        var hashes = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
            rawCodes.Add(code);
            hashes.Add(HashBackupCode(code));
        }

        return (rawCodes, hashes);
    }

    private static List<string> DeserializeBackupCodeHashes(string hashedBackupCodes) =>
        JsonSerializer.Deserialize<List<string>>(hashedBackupCodes) ?? [];

    private static string GenerateNumericCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private bool HasEncryptionKey() =>
        !string.IsNullOrWhiteSpace(_settings.EncryptionKey);

    private IReadOnlyList<byte[]> GetEncryptionKeys() =>
        new[] { _settings.EncryptionKey }
            .Concat(_settings.PreviousEncryptionKeys)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(GetEncryptionKey)
            .ToArray();

    private static byte[] GetEncryptionKey(string key) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private static string GetKeyId(byte[] key) =>
        Convert.ToHexString(SHA256.HashData(key)[..8]).ToLowerInvariant();
}
