using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

public class UserMfaCredential
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public MfaMethod Method { get; set; } = MfaMethod.Totp;
    public string EncryptedTotpSecret { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public DateTime? EnabledAt { get; set; }
    public string? HashedBackupCodes { get; set; }
    public DateTime? BackupCodesRegeneratedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ApplicationUser User { get; set; } = null!;
}
