namespace AuthCenter.Contracts.Responses.Auth;

public class MfaStatusDto
{
    public bool IsEnabled { get; init; }

    /// <summary><c>Totp</c> or <c>EmailOtp</c>; null when no factor was ever set up.</summary>
    public string? Method { get; init; }
    public DateTime? EnabledAt { get; init; }
    public bool HasBackupCodes { get; init; }
    public DateTime? BackupCodesRegeneratedAt { get; init; }
}
