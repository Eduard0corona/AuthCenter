namespace AuthCenter.Contracts.Requests.Common;

public class PaginationQuery
{
    private int _page = 1;
    private int _pageSize = 20;

    public int Page
    {
        get => _page;
        init => _page = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        init => _pageSize = value < 1 ? 1 : value > 100 ? 100 : value;
    }

    public int Skip => (Page - 1) * PageSize;
}
