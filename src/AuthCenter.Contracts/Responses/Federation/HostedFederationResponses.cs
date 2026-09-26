namespace AuthCenter.Contracts.Responses.Federation;

public sealed class FederationDiscoveryResponse
{
    public bool Federated { get; init; }
    public FederationProviderSummary? Provider { get; init; }
}

/// <summary>What the hosted login may show about a provider: never its configuration.</summary>
public sealed class FederationProviderSummary
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
}

public sealed class StartFederationResponse
{
    public string RedirectUrl { get; init; } = string.Empty;
    public int ExpiresIn { get; init; }
}

/// <summary>Values an administrator registers at the upstream identity provider.</summary>
public sealed class FederationServiceProviderResponse
{
    public string? OidcCallbackUrl { get; init; }
    public string? SamlEntityId { get; init; }
    public string? SamlAssertionConsumerServiceUrl { get; init; }
}

public sealed class FederationConnectionTestResponse
{
    public Guid ProviderId { get; init; }
    public string Protocol { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public IReadOnlyList<FederationConnectionCheck> Checks { get; init; } = [];
}

public sealed class FederationConnectionCheck
{
    public string Name { get; init; } = string.Empty;

    /// <summary><c>Pass</c>, <c>Warning</c> or <c>Fail</c>.</summary>
    public string Status { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}
