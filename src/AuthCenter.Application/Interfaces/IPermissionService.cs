using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Requests.Permissions;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Permissions;

namespace AuthCenter.Application.Interfaces;

public interface IPermissionService
{
    Task<PagedResult<PermissionDto>> GetAllAsync(PaginationQuery pagination, CancellationToken ct = default);
    Task<PermissionDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<PermissionDto>> GetByApplicationAsync(Guid applicationSystemId, PaginationQuery pagination, CancellationToken ct = default);
    Task<OperationResult<PermissionDto>> CreateAsync(CreatePermissionRequest request, CancellationToken ct = default);
    Task<OperationResult<PermissionDto>> UpdateAsync(Guid id, UpdatePermissionRequest request, CancellationToken ct = default);
    Task<OperationResult> ActivateAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult> DeactivateAsync(Guid id, CancellationToken ct = default);
}
