namespace AuthCenter.Contracts.Requests.Auth;

public sealed class LinkExternalProviderRequest
{
    public string Provider { get; init; } = string.Empty;
    public string Credential { get; init; } = string.Empty;
}
