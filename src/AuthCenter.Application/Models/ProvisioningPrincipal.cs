namespace AuthCenter.Application.Models;

public sealed record ProvisioningPrincipal(Guid TokenId, Guid ApplicationSystemId, IReadOnlySet<string> Scopes);
