namespace AuthCenter.Contracts.Responses.Auth;

public sealed class PasskeyCredentialDto
{
    public string CredentialId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<string> Transports { get; init; } = [];
    public bool IsUserVerified { get; init; }
    public bool IsBackupEligible { get; init; }
    public bool IsBackedUp { get; init; }
}
