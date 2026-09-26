namespace AuthCenter.Contracts.Responses;

public sealed class AdminApiMetadataDto
{
    public IReadOnlyDictionary<string, string> ErrorCodes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> StepUpPurposes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> OperationPermissions { get; init; } = new Dictionary<string, string>();
    public int MaximumPageSize { get; init; }
    /// <summary>The deployment the console is connected to (<c>AdminConsole:EnvironmentName</c>, else the host environment).</summary>
    public string EnvironmentName { get; init; } = string.Empty;
}
