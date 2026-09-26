namespace AuthCenter.Contracts.Responses.ApiResources;

public class ApiScopeResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
}

public class ApiResourceResponse
{
    /// <summary>Send it back when updating: an update of an older version is rejected with 409.</summary>
    public long Version { get; init; }

    public Guid Id { get; init; }
    public Guid ApplicationSystemId { get; init; }
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public string Identifier { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsActive { get; init; }
    public IReadOnlyList<ApiScopeResponse> Scopes { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}
