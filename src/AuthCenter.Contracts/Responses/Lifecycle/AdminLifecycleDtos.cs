using AuthCenter.Contracts.Responses;
using System.Text.Json;

namespace AuthCenter.Contracts.Responses.Lifecycle;

public sealed class ProvisioningTokenMetadataDto
{
    public Guid Id { get; init; }
    public Guid ApplicationSystemId { get; init; }
    public string ApplicationName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public DateTime? RevokedAt { get; init; }
}

/// <summary>How a provisioning token's SCIM integration is doing, from the requests it made.</summary>
public sealed class ScimDiagnosticsDto
{
    public Guid TokenId { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public DateTime? LastSucceededAt { get; init; }
    public DateTime? LastFailedAt { get; init; }
    public ScimRequestCountsDto Last24Hours { get; init; } = new();
    public ScimRequestCountsDto Last7Days { get; init; } = new();
    /// <summary>The kinds of failure of the last 7 days, most frequent first.</summary>
    public IReadOnlyList<ScimFailureSummaryDto> Failures { get; init; } = [];
}

public sealed class ScimRequestCountsDto
{
    public int Total { get; init; }
    public int Failed { get; init; }
}

public sealed class ScimFailureSummaryDto
{
    public int StatusCode { get; init; }
    public string? ScimType { get; init; }
    public int Count { get; init; }
    public DateTime LastAt { get; init; }
    public string? LastDetail { get; init; }
}

public sealed class ScimRequestLogDto
{
    public Guid Id { get; init; }
    public DateTime CreatedAt { get; init; }
    public string Method { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public int StatusCode { get; init; }
    public string? ScimType { get; init; }
    public string? Detail { get; init; }
    public int DurationMs { get; init; }
    public string? TraceId { get; init; }
}

public sealed class EventHookDto
{
    public Guid Id { get; init; }
    public Guid? ApplicationSystemId { get; init; }
    public string? ApplicationName { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public IReadOnlyList<string> EventTypes { get; init; } = [];
    public bool IsVerified { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? VerifiedAt { get; init; }
    public long Version { get; init; }

    /// <summary>Until when the secret replaced by the last rotation still signs deliveries.</summary>
    public DateTime? PreviousSecretExpiresAt { get; init; }
}

/// <summary>An event type hooks can subscribe to, and the area it belongs to.</summary>
public sealed class EventTypeDto
{
    public string Type { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
}

public sealed class EventHookDeliveryDto
{
    public Guid Id { get; init; }
    public Guid EventId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public Guid HookId { get; init; }
    public string HookName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int AttemptCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime NextAttemptAt { get; init; }
    public DateTime? DeliveredAt { get; init; }
    public DateTime? DeadLetteredAt { get; init; }
    public string? LastError { get; init; }

    /// <summary>The signed JSON body; only in the detail of one delivery.</summary>
    public string? Payload { get; init; }
}

public sealed class ProfileMappingDto
{
    public Guid Id { get; init; }
    public Guid ApplicationSystemId { get; init; }
    public string ApplicationName { get; init; } = string.Empty;
    public string SourceSystem { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public Guid TargetAttributeDefinitionId { get; init; }
    public string TargetAttributeName { get; init; } = string.Empty;
    public bool IsAuthoritative { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public long Version { get; init; }
}

public sealed class ProfileMappingSimulationDto
{
    public bool IsValid { get; init; }
    public string SourcePath { get; init; } = string.Empty;
    public string TargetAttributeName { get; init; } = string.Empty;
    public JsonElement? Value { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed class DynamicGroupRuleDto
{
    public Guid Id { get; init; }
    public Guid DirectoryGroupId { get; init; }
    public string GroupName { get; init; } = string.Empty;
    public Guid ProfileAttributeDefinitionId { get; init; }
    public string AttributeName { get; init; } = string.Empty;
    public string Operator { get; init; } = string.Empty;
    public JsonElement ExpectedValue { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public long Version { get; init; }
}

public sealed class GroupRulePreviewDto
{
    public Guid RuleId { get; init; }
    public PagedResult<GroupRulePreviewUserDto> Users { get; init; } = new();
}

public sealed class GroupRulePreviewUserDto
{
    public Guid Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
}
