using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Lifecycle;
namespace AuthCenter.Application.Interfaces;

public interface ILifecycleAutomationService
{
    Task<OperationResult> CreateProfileMappingAsync(CreateProfileMappingRequest request, CancellationToken ct = default);
    Task<OperationResult> CreateDynamicGroupRuleAsync(CreateDynamicGroupRuleRequest request, CancellationToken ct = default);
    Task<OperationResult> DeleteProfileMappingAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult> DeleteDynamicGroupRuleAsync(Guid id, CancellationToken ct = default);
}
