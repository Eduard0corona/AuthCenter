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
    private readonly IMemoryCache _memoryCache;

    public TotpService(
        AuthCenterDbContext db,
        UserManager<ApplicationUser> userManager,
        IOptions<MfaSettings> settings,
        IDateTimeProvider dateTimeProvider,
        IAuditService auditService,
        IEmailService emailService,
        IMemoryCache memoryCache)
    {
        _db = db;
        _userManager = userManager;
        _settings = settings.Value;
        _dateTimeProvider = dateTimeProvider;
        _auditService = auditService;
        _emailService = emailService;
        _memoryCache = memoryCache;
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

        if (!VerifyTotp(credential, request.TotpCode))
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

        return credential is not null && VerifyTotp(credential, code);
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
        await _db.SaveChangesAsync(ct);

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
            verified = VerifyTotp(credential, request.TotpCode);

        if (!verified && !string.IsNullOrWhiteSpace(request.BackupCode))
            verified = VerifyBackupCode(credential, request.BackupCode);

        if (!verified && !string.IsNullOrWhiteSpace(request.EmailOtpCode) && credential.Method == MfaMethod.EmailOtp)
        {
            var cacheKey = $"emailotp_setup:{userId}";
            if (_memoryCache.TryGetValue(cacheKey, out string? storedCode) && storedCode == request.EmailOtpCode.Trim())
            {
                _memoryCache.Remove(cacheKey);
                verified = true;
            }
        }

        if (!verified)
            return OperationResult.Failure("INVALID_MFA_CODE", "The MFA code is invalid.");

        _db.UserMfaCredentials.Remove(credential);
        await _db.SaveChangesAsync(ct);
        await _auditService.LogAsync("MFA_DISABLED", userId, ct: ct);

        return OperationResult.Success();
    }

    public async Task<OperationResult<BackupCodesResponse>> RegenerateBackupCodesAsync(Guid userId, string totpCode, CancellationToken ct = default)
    {
        var credential = await _db.UserMfaCredentials.FirstOrDefaultAsync(m => m.UserId == userId && m.IsEnabled, ct);
        if (credential is null)
            return OperationResult<BackupCodesResponse>.Failure("MFA_NOT_ENABLED", "MFA is not enabled.");

        if (!VerifyTotp(credential, totpCode))
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
        _memoryCache.Set($"emailotp_setup:{userId}", code, TimeSpan.FromMinutes(10));

        await _emailService.SendMfaEmailOtpAsync(user.Email!, user.FullName, code, ct);
        await _auditService.LogAsync("MFA_EMAIL_OTP_SETUP_SENT", userId, ct: ct);

        return OperationResult.Success();
    }

    public async Task<OperationResult> EnableEmailOtpAsync(Guid userId, EnableEmailMfaRequest request, CancellationToken ct = default)
    {
        var cacheKey = $"emailotp_setup:{userId}";
        if (!_memoryCache.TryGetValue(cacheKey, out string? storedCode))
            return OperationResult.Failure("SETUP_NOT_INITIATED", "No pending email OTP setup. Call /mfa/email-otp/setup first.");

        if (storedCode != request.Code.Trim())
            return OperationResult.Failure("INVALID_CODE", "The verification code is incorrect.");

        _memoryCache.Remove(cacheKey);

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
        _memoryCache.Set($"emailotp_verify:{pendingTokenJti}", code, TimeSpan.FromSeconds(_settings.MfaTokenExpirySeconds));

        await _emailService.SendMfaEmailOtpAsync(user.Email!, user.FullName, code, ct);
        await _auditService.LogAsync("MFA_EMAIL_OTP_SENT", userId, ct: ct);

        return true;
    }

    private bool VerifyTotp(UserMfaCredential credential, string code)
    {
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
                out _,
                new VerificationWindow(previous: 1, future: 1));
        }
        catch
        {
            return false;
        }
    }

    private string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = GetEncryptionKey();
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);
        return Convert.ToBase64String(result);
    }

    private string Decrypt(string ciphertext)
    {
        var payload = Convert.FromBase64String(ciphertext);
        var iv = payload[..16];
        var cipherBytes = payload[16..];

        using var aes = Aes.Create();
        aes.Key = GetEncryptionKey();
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plaintextBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plaintextBytes);
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

    private byte[] GetEncryptionKey() =>
        SHA256.HashData(Encoding.UTF8.GetBytes(_settings.EncryptionKey));
}
