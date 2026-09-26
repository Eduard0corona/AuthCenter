using System.Text.Json.Nodes;

namespace AuthCenter.Application.Models;

/// <summary>A SCIM list request (RFC 7644 §3.4.2): filter, 1-based page and sort.</summary>
public sealed record ScimListRequest(string? Filter, int StartIndex = 1, int Count = 100, string? SortBy = null, string? SortOrder = null);

/// <summary>A SCIM resource: its representation (without <c>meta.location</c>) and its version, the weak ETag.</summary>
public sealed record ScimResource(Guid Id, JsonObject Body, string Version);

public sealed record ScimListResult(int TotalResults, int StartIndex, IReadOnlyList<ScimResource> Resources);

/// <summary>
/// The outcome of presenting a provisioning token. <see cref="TokenId"/> is known whenever the token
/// exists, even when it is refused (revoked, expired, missing the scope), so the request can be
/// recorded for the token's diagnostics.
/// </summary>
public sealed record ProvisioningTokenCheck(ProvisioningPrincipal? Principal, Guid? TokenId, string? Failure)
{
    public const string Missing = "missing";
    public const string Unknown = "unknown";
    public const string Revoked = "revoked";
    public const string Expired = "expired";
    public const string ApplicationInactive = "applicationInactive";
    public const string InsufficientScope = "insufficientScope";
}

/// <summary>One SCIM request to record for a token's diagnostics.</summary>
public sealed record ScimRequestRecord(Guid TokenId, string Method, string Path, int StatusCode, string? ScimType, string? Detail, int DurationMs, string? TraceId);
