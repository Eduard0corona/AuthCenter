namespace AuthCenter.Contracts.Requests.Auth;

public class EnableEmailMfaRequest
{
    public string Code { get; init; } = string.Empty;
}
