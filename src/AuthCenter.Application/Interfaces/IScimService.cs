using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Models;

namespace AuthCenter.Application.Interfaces;

/// <summary>
/// SCIM 2.0 Users and Groups of one application (RFC 7643/7644). Failures carry the SCIM error type as
/// their code (<c>notFound</c>, <c>uniqueness</c>, <c>preconditionFailed</c>, <c>invalidValue</c>…).
/// Writes accept the client's <c>If-Match</c>, compared with the resource's current version.
/// </summary>
public interface IScimService
{
    Task<OperationResult<ScimListResult>> ListUsersAsync(ProvisioningPrincipal principal, ScimListRequest request, CancellationToken ct = default);
    Task<OperationResult<ScimResource>> GetUserAsync(ProvisioningPrincipal principal, Guid id, CancellationToken ct = default);
    Task<OperationResult<ScimResource>> CreateUserAsync(ProvisioningPrincipal principal, JsonElement payload, CancellationToken ct = default);
    Task<OperationResult<ScimResource>> ReplaceUserAsync(ProvisioningPrincipal principal, Guid id, JsonElement payload, string? ifMatch, CancellationToken ct = default);
    Task<OperationResult<ScimResource>> PatchUserAsync(ProvisioningPrincipal principal, Guid id, JsonElement payload, string? ifMatch, CancellationToken ct = default);
    Task<OperationResult> DeleteUserAsync(ProvisioningPrincipal principal, Guid id, string? ifMatch, CancellationToken ct = default);
    Task<OperationResult<ScimListResult>> ListGroupsAsync(ProvisioningPrincipal principal, ScimListRequest request, CancellationToken ct = default);
    Task<OperationResult<ScimResource>> GetGroupAsync(ProvisioningPrincipal principal, Guid id, CancellationToken ct = default);
    Task<OperationResult<ScimResource>> CreateGroupAsync(ProvisioningPrincipal principal, JsonElement payload, CancellationToken ct = default);
    Task<OperationResult<ScimResource>> ReplaceGroupAsync(ProvisioningPrincipal principal, Guid id, JsonElement payload, string? ifMatch, CancellationToken ct = default);
    Task<OperationResult<ScimResource>> PatchGroupAsync(ProvisioningPrincipal principal, Guid id, JsonElement payload, string? ifMatch, CancellationToken ct = default);
    Task<OperationResult> DeleteGroupAsync(ProvisioningPrincipal principal, Guid id, string? ifMatch, CancellationToken ct = default);
}
