namespace AuthCenter.Contracts.Requests.Federation;

/// <summary>
/// Home realm discovery from the hosted login. The target application comes from the
/// authorization interaction or, for a direct sign-in, from its code.
/// </summary>
public sealed class FederationDiscoveryRequest
{
    public string? Email { get; init; }
    public string? Domain { get; init; }
    public string? InteractionId { get; init; }
    public string? ApplicationCode { get; init; }
}

/// <summary>Starts a browser redirect to an upstream identity provider from the hosted login.</summary>
public sealed class StartFederationRequest
{
    public Guid ProviderId { get; init; }
    public string? InteractionId { get; init; }
    public string? ApplicationCode { get; init; }
    public string? ReturnUrl { get; init; }
    public string? LoginHint { get; init; }
}

/// <summary>Redeems the single-use result the upstream callback left for this browser.</summary>
public sealed class FederationResultRequest
{
    public string Handle { get; init; } = string.Empty;
}
