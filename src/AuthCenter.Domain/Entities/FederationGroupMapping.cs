namespace AuthCenter.Domain.Entities;

/// <summary>
/// Maps a value of the provider's groups claim to a directory group. The provider is authoritative
/// for its mapped groups: each federated sign-in adds and removes those memberships.
/// </summary>
public sealed class FederationGroupMapping
{
    public Guid Id { get; set; }
    public Guid FederationProviderId { get; set; }
    public string UpstreamValue { get; set; } = string.Empty;
    public Guid DirectoryGroupId { get; set; }
    public FederationProvider FederationProvider { get; set; } = null!;
    public DirectoryGroup DirectoryGroup { get; set; } = null!;
}
