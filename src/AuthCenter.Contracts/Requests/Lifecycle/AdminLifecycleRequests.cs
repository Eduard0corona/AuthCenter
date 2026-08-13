using AuthCenter.Contracts.Requests.Common;
using System.Text.Json;

namespace AuthCenter.Contracts.Requests.Lifecycle;

public sealed class ProvisioningTokenQuery : PaginationQuery
{
    public Guid? ApplicationSystemId { get; init; }
    public string? Status { get; init; }
}

public sealed class UpdateEventHookRequest
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public IReadOnlyList<string> EventTypes { get; init; } = [];
    public bool IsActive { get; init; }
    public long Version { get; init; }
}

public sealed class EventHookQuery : PaginationQuery
{
    public Guid? ApplicationSystemId { get; init; }
    public bool? IsActive { get; init; }
    public bool? IsVerified { get; init; }
    public string? Search { get; init; }
}

public sealed class EventHookDeliveryQuery : PaginationQuery
{
    public Guid? HookId { get; init; }
    public string? Status { get; init; }
    public string? EventType { get; init; }
    public Guid? EventId { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
}

public sealed class UpdateProfileMappingRequest
{
    public string SourceSystem { get; init; } = "SCIM";
    public string SourcePath { get; init; } = string.Empty;
    public Guid TargetAttributeDefinitionId { get; init; }
    public bool IsAuthoritative { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }
}

public sealed class ProfileMappingQuery : PaginationQuery
{
    public Guid? ApplicationSystemId { get; init; }
    public bool? IsActive { get; init; }
}

public sealed class ProfileMappingSimulationRequest
{
    public JsonElement SourceDocument { get; init; }
}

public sealed class UpdateDynamicGroupRuleRequest
{
    public Guid ProfileAttributeDefinitionId { get; init; }
    public string Operator { get; init; } = "eq";
    public JsonElement ExpectedValue { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }
}

public sealed class DynamicGroupRuleQuery : PaginationQuery
{
    public Guid? DirectoryGroupId { get; init; }
    public bool? IsActive { get; init; }
}

public sealed class GroupRulePreviewRequest : PaginationQuery { }
