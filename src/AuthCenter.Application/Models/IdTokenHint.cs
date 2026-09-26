namespace AuthCenter.Application.Models;

/// <summary>What an ID token this server issued says about its user, client and session.</summary>
public sealed record IdTokenHint(string Subject, string ClientId, Guid? SessionId);
