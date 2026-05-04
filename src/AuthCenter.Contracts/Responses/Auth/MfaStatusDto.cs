namespace AuthCenter.Contracts.Responses.Auth;

public class MfaStatusDto
{
    public bool IsEnabled { get; init; }
    public DateTime? EnabledAt { get; init; }
    public bool HasBackupCodes { get; init; }
    public DateTime? BackupCodesRegeneratedAt { get; init; }
}
