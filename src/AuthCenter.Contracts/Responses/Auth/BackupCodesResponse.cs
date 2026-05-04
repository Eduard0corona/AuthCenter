namespace AuthCenter.Contracts.Responses.Auth;

public class BackupCodesResponse
{
    public IReadOnlyList<string> Codes { get; init; } = [];
}
