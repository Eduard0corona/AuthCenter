namespace AuthCenter.Contracts.Responses.OAuth;

/// <summary>One scope a client asks for, described in the words the consent screen shows.</summary>
public sealed class ScopeDescription
{
    public string Scope { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>The API's longer explanation of the scope, when it has one.</summary>
    public string? Detail { get; init; }
}
