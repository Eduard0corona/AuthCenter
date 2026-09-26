namespace AuthCenter.Domain.Entities;

/// <summary>A permission an OAuth client can request for an API, such as orders.read.</summary>
public class ApiScope
{
    public Guid Id { get; set; }
    public Guid ApiResourceId { get; set; }

    /// <summary>Unique across all APIs, so a requested scope always names exactly one resource.</summary>
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }

    public ApiResource ApiResource { get; set; } = null!;
}
