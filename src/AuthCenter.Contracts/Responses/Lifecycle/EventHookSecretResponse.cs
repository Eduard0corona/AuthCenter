namespace AuthCenter.Contracts.Responses.Lifecycle;

public sealed class EventHookSecretResponse { public Guid Id { get; init; } public string Secret { get; init; } = string.Empty; public bool IsVerified { get; init; } }
