using AuthCenter.Contracts.Requests.Common;

namespace AuthCenter.Contracts.Requests.Groups;

public class DirectoryGroupQuery : PaginationQuery
{
    public string? Search { get; init; }
    public bool? IsActive { get; init; }
}
