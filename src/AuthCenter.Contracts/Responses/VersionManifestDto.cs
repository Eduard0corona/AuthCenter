namespace AuthCenter.Contracts.Responses;

public sealed class VersionManifestDto
{
    public string Version { get; init; } = string.Empty;
    public string? Commit { get; init; }
    public string AdminFrontendBasePath { get; init; } = "/admin-v2";
    public int ContractVersion { get; init; } = 1;
}
