namespace AuthCenter.Application.Models;

/// <summary>
/// The browser taking part in a hosted federated sign-in: its binding cookie, the user it is
/// already signed in as (if any) and its network signals.
/// </summary>
public sealed record FederationCaller(string? BrowserBinding, Guid? SignedInUserId, string? IpAddress, string? UserAgent);

/// <summary>
/// What an upstream callback produced: where the browser continues in the hosted login, or, for a
/// flow started through the JSON API, the sign-in result itself.
/// </summary>
public sealed record FederationCompletion(string? RedirectPath, Common.OperationResult<Contracts.Responses.Auth.AuthResponse>? Result);
