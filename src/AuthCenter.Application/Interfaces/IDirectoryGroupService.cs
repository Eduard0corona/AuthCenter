using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Groups;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Groups;

namespace AuthCenter.Application.Interfaces;

public interface IDirectoryGroupService
{
    Task<PagedResult<DirectoryGroupDto>> GetAllAsync(DirectoryGroupQuery query, CancellationToken ct = default);
    Task<DirectoryGroupDto?> GetByIdAsync(Guid groupId, CancellationToken ct = default);
    Task<PagedResult<DirectoryGroupMemberDto>?> GetMembersAsync(Guid groupId, int page, int pageSize, CancellationToken ct = default);
    Task<OperationResult<DirectoryGroupDto>> CreateAsync(CreateDirectoryGroupRequest request, CancellationToken ct = default);
    Task<OperationResult<DirectoryGroupDto>> UpdateAsync(Guid groupId, UpdateDirectoryGroupRequest request, CancellationToken ct = default);
    Task<OperationResult> ActivateAsync(Guid groupId, CancellationToken ct = default);
    Task<OperationResult> DeactivateAsync(Guid groupId, CancellationToken ct = default);
    Task<OperationResult> AddMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default);
    Task<OperationResult> RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default);
    Task<OperationResult> AssignApplicationAsync(Guid groupId, Guid applicationSystemId, CancellationToken ct = default);
    Task<OperationResult> RemoveApplicationAsync(Guid groupId, Guid applicationSystemId, CancellationToken ct = default);
    Task<OperationResult> AssignRoleAsync(Guid groupId, Guid roleId, CancellationToken ct = default);
    Task<OperationResult> RemoveRoleAsync(Guid groupId, Guid roleId, CancellationToken ct = default);
}
