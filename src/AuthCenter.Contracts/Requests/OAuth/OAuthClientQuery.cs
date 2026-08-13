using AuthCenter.Contracts.Requests.Common;

namespace AuthCenter.Contracts.Requests.OAuth;

public class OAuthClientQuery : PaginationQuery
{
    public string? Search { get; init; }
    public Guid? ApplicationSystemId { get; init; }
    public int? ClientType { get; init; }
    public bool? IsActive { get; init; }
}
