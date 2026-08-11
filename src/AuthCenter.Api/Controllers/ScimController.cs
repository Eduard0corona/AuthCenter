using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController, AllowAnonymous, Route("scim/v2")]
public sealed class ScimController : ControllerBase
{
    private const string ScimContentType = "application/scim+json";
    private readonly IProvisioningTokenService _tokens; private readonly IScimService _scim;
    public ScimController(IProvisioningTokenService tokens, IScimService scim) { _tokens = tokens; _scim = scim; }

    [HttpGet("ServiceProviderConfig")] public IActionResult ServiceProviderConfig() => Scim(new { schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig" }, patch = new { supported = true }, bulk = new { supported = false }, filter = new { supported = true, maxResults = 200 }, changePassword = new { supported = false }, sort = new { supported = false }, etag = new { supported = false }, authenticationSchemes = new[] { new { type = "oauthbearertoken", name = "Bearer Token", description = "Scoped, expiring AuthCenter provisioning token" } } });
    [HttpGet("ResourceTypes")] public IActionResult ResourceTypes() => Scim(new { schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" }, totalResults = 2, Resources = new[] { new { id = "User", name = "User", endpoint = "/Users", schema = "urn:ietf:params:scim:schemas:core:2.0:User" }, new { id = "Group", name = "Group", endpoint = "/Groups", schema = "urn:ietf:params:scim:schemas:core:2.0:Group" } } });

    [HttpGet("Users")] public async Task<IActionResult> ListUsers([FromQuery] string? filter = null, [FromQuery] int startIndex = 1, [FromQuery] int count = 100, CancellationToken ct = default) { var p = await Principal("scim.users.read", ct); if (p is null) return UnauthorizedScim(); return Result(await _scim.ListUsersAsync(p, filter, startIndex, count, ct)); }
    [HttpGet("Users/{id:guid}")] public async Task<IActionResult> GetUser(Guid id, CancellationToken ct) { var p = await Principal("scim.users.read", ct); if (p is null) return UnauthorizedScim(); return Result(await _scim.GetUserAsync(p, id, ct)); }
    [HttpPost("Users")] public async Task<IActionResult> CreateUser([FromBody] JsonElement body, CancellationToken ct) { var p = await Principal("scim.users.write", ct); if (p is null) return UnauthorizedScim(); var r = await _scim.CreateUserAsync(p, body, ct); return r.IsSuccess ? Scim(r.Data!, 201) : Error(r); }
    [HttpPatch("Users/{id:guid}")] public async Task<IActionResult> PatchUser(Guid id, [FromBody] JsonElement body, CancellationToken ct) { var p = await Principal("scim.users.write", ct); if (p is null) return UnauthorizedScim(); return Result(await _scim.PatchUserAsync(p, id, body, ct)); }
    [HttpDelete("Users/{id:guid}")] public async Task<IActionResult> DeleteUser(Guid id, CancellationToken ct) { var p = await Principal("scim.users.write", ct); if (p is null) return UnauthorizedScim(); var r = await _scim.DeleteUserAsync(p, id, ct); return r.IsSuccess ? NoContent() : Error(r); }

    [HttpGet("Groups")] public async Task<IActionResult> ListGroups([FromQuery] string? filter = null, [FromQuery] int startIndex = 1, [FromQuery] int count = 100, CancellationToken ct = default) { var p = await Principal("scim.groups.read", ct); if (p is null) return UnauthorizedScim(); return Result(await _scim.ListGroupsAsync(p, filter, startIndex, count, ct)); }
    [HttpGet("Groups/{id:guid}")] public async Task<IActionResult> GetGroup(Guid id, CancellationToken ct) { var p = await Principal("scim.groups.read", ct); if (p is null) return UnauthorizedScim(); return Result(await _scim.GetGroupAsync(p, id, ct)); }
    [HttpPost("Groups")] public async Task<IActionResult> CreateGroup([FromBody] JsonElement body, CancellationToken ct) { var p = await Principal("scim.groups.write", ct); if (p is null) return UnauthorizedScim(); var r = await _scim.CreateGroupAsync(p, body, ct); return r.IsSuccess ? Scim(r.Data!, 201) : Error(r); }
    [HttpPatch("Groups/{id:guid}")] public async Task<IActionResult> PatchGroup(Guid id, [FromBody] JsonElement body, CancellationToken ct) { var p = await Principal("scim.groups.write", ct); if (p is null) return UnauthorizedScim(); return Result(await _scim.PatchGroupAsync(p, id, body, ct)); }
    [HttpDelete("Groups/{id:guid}")] public async Task<IActionResult> DeleteGroup(Guid id, CancellationToken ct) { var p = await Principal("scim.groups.write", ct); if (p is null) return UnauthorizedScim(); var r = await _scim.DeleteGroupAsync(p, id, ct); return r.IsSuccess ? NoContent() : Error(r); }

    private async Task<ProvisioningPrincipal?> Principal(string scope, CancellationToken ct) { var header = Request.Headers.Authorization.ToString(); return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await _tokens.ValidateAsync(header[7..].Trim(), scope, ct) : null; }
    private IActionResult Result(OperationResult<object> result) => result.IsSuccess ? Scim(result.Data!) : Error(result);
    private IActionResult Error(OperationResult result) => Error(result.ErrorCode, result.Message);
    private IActionResult Error(OperationResult<object> result) => Error(result.ErrorCode, result.Message);
    private IActionResult Error(string code, string message) { var status = code == "notFound" ? 404 : code == "uniqueness" ? 409 : 400; return Scim(new { schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:Error" }, status = status.ToString(), scimType = code, detail = message }, status); }
    private IActionResult UnauthorizedScim() => Scim(new { schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:Error" }, status = "401", detail = "A valid provisioning token with the required scope is required." }, 401);
    private ContentResult Scim(object value, int status = 200) => new() { Content = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ContentType = ScimContentType, StatusCode = status };
}
