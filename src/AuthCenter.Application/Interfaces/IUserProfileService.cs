using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Profiles;
using AuthCenter.Contracts.Responses.Profiles;

namespace AuthCenter.Application.Interfaces;

public interface IUserProfileService
{
    Task<IReadOnlyList<ProfileAttributeDefinitionDto>> GetSchemaAsync(bool includeInactive, CancellationToken ct = default);
    Task<OperationResult<ProfileAttributeDefinitionDto>> CreateDefinitionAsync(CreateProfileAttributeDefinitionRequest request, CancellationToken ct = default);
    Task<OperationResult<ProfileAttributeDefinitionDto>> UpdateDefinitionAsync(Guid definitionId, UpdateProfileAttributeDefinitionRequest request, CancellationToken ct = default);
    Task<OperationResult> DeactivateDefinitionAsync(Guid definitionId, CancellationToken ct = default);
    Task<OperationResult<UserProfileDto>> GetUserProfileAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult<UserProfileDto>> UpdateUserProfileAsync(Guid userId, UpdateUserProfileRequest request, CancellationToken ct = default);
}
