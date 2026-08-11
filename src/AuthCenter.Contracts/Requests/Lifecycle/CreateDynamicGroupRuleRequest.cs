using System.Text.Json;
namespace AuthCenter.Contracts.Requests.Lifecycle;

public sealed class CreateDynamicGroupRuleRequest
{
    public Guid DirectoryGroupId { get; init; }
    public Guid ProfileAttributeDefinitionId { get; init; }
    public string Operator { get; init; } = "eq";
    public JsonElement ExpectedValue { get; init; }
}
