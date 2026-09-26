namespace AuthCenter.Domain.Entities;

/// <summary>
/// One SCIM request made with a provisioning token, kept so operators can diagnose an integration:
/// what was called, how it ended and why it failed. Payloads are never stored.
/// </summary>
public sealed class ScimRequestLog
{
    public Guid Id { get; set; }
    public Guid ProvisioningTokenId { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string? ScimType { get; set; }
    public string? Detail { get; set; }
    public int DurationMs { get; set; }
    public string? TraceId { get; set; }
    public DateTime CreatedAt { get; set; }
    public ProvisioningToken ProvisioningToken { get; set; } = null!;
}
