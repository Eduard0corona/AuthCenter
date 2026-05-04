namespace AuthCenter.Contracts.Requests.Auth;

public class DeleteAccountRequest
{
    public string? Password { get; init; }
    public bool ConfirmDeletion { get; init; }
}
