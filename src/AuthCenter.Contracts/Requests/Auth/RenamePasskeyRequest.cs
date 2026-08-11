namespace AuthCenter.Contracts.Requests.Auth;

public sealed class RenamePasskeyRequest
{
    public string Name { get; init; } = string.Empty;
}
