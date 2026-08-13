using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Lifecycle;
namespace AuthCenter.Application.Interfaces;

public interface ILifecycleAutomationService
{
    Task<PagedResult<ProfileMappingDto>> GetProfileMappingsAsync(ProfileMappingQuery query, CancellationToken ct = default);
    Task<ProfileMappingDto?> GetProfileMappingAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult<ProfileMappingDto>> CreateProfileMappingAsync(CreateProfileMappingRequest request, CancellationToken ct = default);
    Task<OperationResult> ValidateProfileMappingAsync(CreateProfileMappingRequest request, CancellationToken ct = default);
    Task<OperationResult<ProfileMappingDto>> UpdateProfileMappingAsync(Guid id, UpdateProfileMappingRequest request, CancellationToken ct = default);
    Task<OperationResult<ProfileMappingSimulationDto>> SimulateProfileMappingAsync(Guid id, ProfileMappingSimulationRequest request, CancellationToken ct = default);
    Task<PagedResult<DynamicGroupRuleDto>> GetDynamicGroupRulesAsync(DynamicGroupRuleQuery query, CancellationToken ct = default);
    Task<DynamicGroupRuleDto?> GetDynamicGroupRuleAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult<DynamicGroupRuleDto>> CreateDynamicGroupRuleAsync(CreateDynamicGroupRuleRequest request, CancellationToken ct = default);
    Task<OperationResult<DynamicGroupRuleDto>> UpdateDynamicGroupRuleAsync(Guid id, UpdateDynamicGroupRuleRequest request, CancellationToken ct = default);
    Task<OperationResult<GroupRulePreviewDto>> PreviewDynamicGroupRuleAsync(Guid id, GroupRulePreviewRequest request, CancellationToken ct = default);
    Task<OperationResult> DeleteProfileMappingAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult> DeleteDynamicGroupRuleAsync(Guid id, CancellationToken ct = default);
}
