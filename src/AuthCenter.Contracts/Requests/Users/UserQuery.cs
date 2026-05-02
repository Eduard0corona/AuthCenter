using AuthCenter.Contracts.Requests.Common;

namespace AuthCenter.Contracts.Requests.Users;

public class UserQuery : PaginationQuery
{
    public string? Search { get; init; }
    public Guid? ApplicationSystemId { get; init; }
    public bool? IsActive { get; init; }
    public bool? HasPendingAccess { get; init; }
}
