namespace AuthCenter.Contracts.Requests.OAuth;

public class AuthorizeRequest
{
    public string? ResponseType { get; init; }
    public string? ClientId { get; init; }
    public string? RedirectUri { get; init; }
    public string? Scope { get; init; }
    public string? State { get; init; }
    public string? CodeChallenge { get; init; }
    public string? CodeChallengeMethod { get; init; }
    public string? Nonce { get; init; }
    public string? Prompt { get; init; }
    public string? MaxAge { get; init; }
    public string? LoginHint { get; init; }
    public string? IdTokenHint { get; init; }
    public string? AcrValues { get; init; }
    public string? ResponseMode { get; init; }
    public string? UiLocales { get; init; }
    public string? Request { get; init; }
    public string? RequestUri { get; init; }
}
