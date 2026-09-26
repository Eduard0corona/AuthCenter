namespace AuthCenter.Contracts.Requests.ApiResources;

public class ApiScopeRequest
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
}

public class CreateApiResourceRequest
{
    public Guid ApplicationSystemId { get; init; }

    /// <summary>Absolute URI used as resource indicator and token audience.</summary>
    public string Identifier { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public IList<ApiScopeRequest> Scopes { get; init; } = [];
}

/// <summary>Replaces the API's descriptive fields and its complete scope list.</summary>
public class UpdateApiResourceRequest
{
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsActive { get; init; } = true;
    public IList<ApiScopeRequest> Scopes { get; init; } = [];
}

public class ApiResourceQuery
{
    public string? Search { get; init; }
    public Guid? ApplicationSystemId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
