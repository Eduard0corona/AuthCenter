namespace AuthCenter.Contracts.Requests.Lifecycle;

public sealed class CreateProfileMappingRequest
{
    public Guid ApplicationSystemId { get; init; }
    public string SourceSystem { get; init; } = "SCIM";
    public string SourcePath { get; init; } = string.Empty;
    public Guid TargetAttributeDefinitionId { get; init; }
    public bool IsAuthoritative { get; init; }
}
