namespace AuthCenter.Contracts.Responses;

public sealed class AdminApiMetadataDto
{
    public IReadOnlyDictionary<string, string> ErrorCodes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> StepUpPurposes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> OperationPermissions { get; init; } = new Dictionary<string, string>();
    public int MaximumPageSize { get; init; }
}
