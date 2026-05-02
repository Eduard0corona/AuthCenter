using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Requests.Roles;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Roles;

namespace AuthCenter.Application.Interfaces;

public interface IRoleService
{
    Task<PagedResult<RoleDto>> GetAllAsync(PaginationQuery pagination, CancellationToken ct = default);
    Task<RoleDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult<RoleDto>> CreateAsync(CreateRoleRequest request, CancellationToken ct = default);
    Task<OperationResult<RoleDto>> UpdateAsync(Guid id, UpdateRoleRequest request, CancellationToken ct = default);
    Task<OperationResult> AddPermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default);
    Task<OperationResult> RemovePermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default);
    Task<IList<string>> GetPermissionCodesForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IList<string>> GetRoleNamesForUserAsync(Guid userId, CancellationToken ct = default);
}
