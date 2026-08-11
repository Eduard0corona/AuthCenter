namespace AuthCenter.Contracts.Requests.Lifecycle;

public sealed class CreateEventHookRequest
{
    public Guid? ApplicationSystemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public IReadOnlyList<string> EventTypes { get; init; } = [];
}
