using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Models;

namespace AuthCenter.Application.Interfaces;

public interface IScimService
{
    Task<OperationResult<object>> ListUsersAsync(ProvisioningPrincipal principal, string? filter, int startIndex, int count, CancellationToken ct = default);
    Task<OperationResult<object>> GetUserAsync(ProvisioningPrincipal principal, Guid id, CancellationToken ct = default);
    Task<OperationResult<object>> CreateUserAsync(ProvisioningPrincipal principal, JsonElement payload, CancellationToken ct = default);
    Task<OperationResult<object>> PatchUserAsync(ProvisioningPrincipal principal, Guid id, JsonElement payload, CancellationToken ct = default);
    Task<OperationResult> DeleteUserAsync(ProvisioningPrincipal principal, Guid id, CancellationToken ct = default);
    Task<OperationResult<object>> ListGroupsAsync(ProvisioningPrincipal principal, string? filter, int startIndex, int count, CancellationToken ct = default);
    Task<OperationResult<object>> GetGroupAsync(ProvisioningPrincipal principal, Guid id, CancellationToken ct = default);
    Task<OperationResult<object>> CreateGroupAsync(ProvisioningPrincipal principal, JsonElement payload, CancellationToken ct = default);
    Task<OperationResult<object>> PatchGroupAsync(ProvisioningPrincipal principal, Guid id, JsonElement payload, CancellationToken ct = default);
    Task<OperationResult> DeleteGroupAsync(ProvisioningPrincipal principal, Guid id, CancellationToken ct = default);
}
