namespace AuthCenter.Contracts.Requests.Users;

public class UpdateUserRequest
{
    /// <summary>The version the caller loaded; when sent, a record changed since then is not overwritten (409).</summary>
    public long? Version { get; init; }

    public string FullName { get; init; } = string.Empty;
    public string? PictureUrl { get; init; }
}
