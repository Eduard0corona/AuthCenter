using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Users;

namespace AuthCenter.Application.Interfaces;

public interface IUserAccessService
{
    Task<PagedResult<UserDto>> GetAllUsersAsync(PaginationQuery pagination, CancellationToken ct = default);
    Task<UserDto?> GetUserByIdAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult> GrantAccessAsync(Guid userId, Guid applicationSystemId, bool isActive = true, CancellationToken ct = default);
    Task<OperationResult> RevokeAccessAsync(Guid userId, Guid applicationSystemId, CancellationToken ct = default);
    Task<OperationResult> AssignRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default);
    Task<OperationResult> RemoveRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default);
    Task<OperationResult> ActivateUserAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult> DeactivateUserAsync(Guid userId, CancellationToken ct = default);
    Task<bool> HasActiveAccessAsync(Guid userId, Guid applicationSystemId, CancellationToken ct = default);
    Task<IList<string>> GetApplicationCodesForUserAsync(Guid userId, CancellationToken ct = default);
}
