using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Infrastructure.Services.Scim;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace AuthCenter.Api.Controllers;

/// <summary>
/// SCIM 2.0 (RFC 7644) for provisioning clients, authenticated with a provisioning token. Every request
/// made with a known token is recorded for its diagnostics in the console, successful or not.
/// </summary>
[ApiController, AllowAnonymous, Route("scim/v2")]
public sealed class ScimController : ControllerBase
{
    private const string ScimContentType = "application/scim+json";
    private const string ErrorSchema = "urn:ietf:params:scim:api:messages:2.0:Error";
    private readonly IProvisioningTokenService _tokens;
    private readonly IScimService _scim;
    private readonly ILogger<ScimController> _logger;
    private string? _failureType;
    private string? _failureDetail;

    public ScimController(IProvisioningTokenService tokens, IScimService scim, ILogger<ScimController> logger)
    {
        _tokens = tokens;
        _scim = scim;
        _logger = logger;
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}{Request.PathBase}/scim/v2";

    // Discovery (RFC 7644 §4): public, it describes the service and holds no data.

    [HttpGet("ServiceProviderConfig")]
    public IActionResult ServiceProviderConfig() => Scim(ScimDiscovery.ServiceProviderConfig(BaseUrl));

    [HttpGet("ResourceTypes")]
    public IActionResult ResourceTypes() => Scim(ListOf(ScimDiscovery.ResourceTypes(BaseUrl)));

    [HttpGet("ResourceTypes/{id}")]
    public IActionResult ResourceType(string id) =>
        ScimDiscovery.ResourceType(id, BaseUrl) is { } type ? Scim(type) : Failure("notFound", $"Resource type {id} not found.");

    [HttpGet("Schemas")]
    public IActionResult Schemas() => Scim(ListOf(ScimDiscovery.Schemas(BaseUrl)));

    [HttpGet("Schemas/{id}")]
    public IActionResult Schema(string id) =>
        ScimDiscovery.Schema(id, BaseUrl) is { } schema ? Scim(schema) : Failure("notFound", $"Schema {id} not found.");

    // Users

    [HttpGet("Users")]
    public Task<IActionResult> ListUsers([FromQuery] string? filter = null, [FromQuery] int startIndex = 1, [FromQuery] int count = 100,
        [FromQuery] string? sortBy = null, [FromQuery] string? sortOrder = null, [FromQuery] string? attributes = null,
        [FromQuery] string? excludedAttributes = null, CancellationToken ct = default) =>
        RunAsync("scim.users.read", async principal => List("Users",
            await _scim.ListUsersAsync(principal, new ScimListRequest(filter, startIndex, count, sortBy, sortOrder), ct), attributes, excludedAttributes), ct);

    [HttpGet("Users/{id:guid}")]
    public Task<IActionResult> GetUser(Guid id, [FromQuery] string? attributes = null, [FromQuery] string? excludedAttributes = null, CancellationToken ct = default) =>
        RunAsync("scim.users.read", async principal => Resource("Users", await _scim.GetUserAsync(principal, id, ct), attributes, excludedAttributes, conditional: true), ct);

    [HttpPost("Users")]
    public Task<IActionResult> CreateUser([FromBody] JsonElement body, [FromQuery] string? attributes = null, [FromQuery] string? excludedAttributes = null, CancellationToken ct = default) =>
        RunAsync("scim.users.write", async principal => Resource("Users", await _scim.CreateUserAsync(principal, body, ct), attributes, excludedAttributes, StatusCodes.Status201Created), ct);

    [HttpPut("Users/{id:guid}")]
    public Task<IActionResult> ReplaceUser(Guid id, [FromBody] JsonElement body, [FromQuery] string? attributes = null, [FromQuery] string? excludedAttributes = null, CancellationToken ct = default) =>
        RunAsync("scim.users.write", async principal => Resource("Users", await _scim.ReplaceUserAsync(principal, id, body, IfMatch, ct), attributes, excludedAttributes), ct);

    [HttpPatch("Users/{id:guid}")]
    public Task<IActionResult> PatchUser(Guid id, [FromBody] JsonElement body, [FromQuery] string? attributes = null, [FromQuery] string? excludedAttributes = null, CancellationToken ct = default) =>
        RunAsync("scim.users.write", async principal => Resource("Users", await _scim.PatchUserAsync(principal, id, body, IfMatch, ct), attributes, excludedAttributes), ct);

    [HttpDelete("Users/{id:guid}")]
    public Task<IActionResult> DeleteUser(Guid id, CancellationToken ct = default) =>
        RunAsync("scim.users.write", async principal => Deleted(await _scim.DeleteUserAsync(principal, id, IfMatch, ct)), ct);

    // Groups

    [HttpGet("Groups")]
    public Task<IActionResult> ListGroups([FromQuery] string? filter = null, [FromQuery] int startIndex = 1, [FromQuery] int count = 100,
        [FromQuery] string? sortBy = null, [FromQuery] string? sortOrder = null, [FromQuery] string? attributes = null,
        [FromQuery] string? excludedAttributes = null, CancellationToken ct = default) =>
        RunAsync("scim.groups.read", async principal => List("Groups",
            await _scim.ListGroupsAsync(principal, new ScimListRequest(filter, startIndex, count, sortBy, sortOrder), ct), attributes, excludedAttributes), ct);

    [HttpGet("Groups/{id:guid}")]
    public Task<IActionResult> GetGroup(Guid id, [FromQuery] string? attributes = null, [FromQuery] string? excludedAttributes = null, CancellationToken ct = default) =>
        RunAsync("scim.groups.read", async principal => Resource("Groups", await _scim.GetGroupAsync(principal, id, ct), attributes, excludedAttributes, conditional: true), ct);

    [HttpPost("Groups")]
    public Task<IActionResult> CreateGroup([FromBody] JsonElement body, [FromQuery] string? attributes = null, [FromQuery] string? excludedAttributes = null, CancellationToken ct = default) =>
        RunAsync("scim.groups.write", async principal => Resource("Groups", await _scim.CreateGroupAsync(principal, body, ct), attributes, excludedAttributes, StatusCodes.Status201Created), ct);

    [HttpPut("Groups/{id:guid}")]
    public Task<IActionResult> ReplaceGroup(Guid id, [FromBody] JsonElement body, [FromQuery] string? attributes = null, [FromQuery] string? excludedAttributes = null, CancellationToken ct = default) =>
        RunAsync("scim.groups.write", async principal => Resource("Groups", await _scim.ReplaceGroupAsync(principal, id, body, IfMatch, ct), attributes, excludedAttributes), ct);

    [HttpPatch("Groups/{id:guid}")]
    public Task<IActionResult> PatchGroup(Guid id, [FromBody] JsonElement body, [FromQuery] string? attributes = null, [FromQuery] string? excludedAttributes = null, CancellationToken ct = default) =>
        RunAsync("scim.groups.write", async principal => Resource("Groups", await _scim.PatchGroupAsync(principal, id, body, IfMatch, ct), attributes, excludedAttributes), ct);

    [HttpDelete("Groups/{id:guid}")]
    public Task<IActionResult> DeleteGroup(Guid id, CancellationToken ct = default) =>
        RunAsync("scim.groups.write", async principal => Deleted(await _scim.DeleteGroupAsync(principal, id, IfMatch, ct)), ct);

    private string? IfMatch => Request.Headers.IfMatch.Count == 0 ? null : Request.Headers.IfMatch.ToString();

    /// <summary>Authenticates the token for the scope, runs the operation and records the request.</summary>
    private async Task<IActionResult> RunAsync(string scope, Func<ProvisioningPrincipal, Task<IActionResult>> operation, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var header = Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        var check = await _tokens.AuthenticateAsync(token, scope, ct);
        IActionResult result;
        try
        {
            result = check.Principal is null ? Refused(check, scope) : await operation(check.Principal);
        }
        catch (Exception exception) when (check.TokenId.HasValue && !ct.IsCancellationRequested)
        {
            await RecordAsync(check.TokenId.Value, StatusCodes.Status500InternalServerError, "An unexpected error occurred.", started);
            _logger.LogError(exception, "SCIM {Method} {Path} failed", Request.Method, Request.Path);
            throw;
        }
        if (check.TokenId is { } tokenId)
            await RecordAsync(tokenId, StatusOf(result), null, started);
        return result;
    }

    private async Task RecordAsync(Guid tokenId, int status, string? unexpected, long started)
    {
        try
        {
            var traceId = Activity.Current?.TraceId.ToHexString() ?? HttpContext.TraceIdentifier;
            await _tokens.RecordRequestAsync(new ScimRequestRecord(tokenId, Request.Method, Request.Path.Value ?? string.Empty, status,
                _failureType, unexpected ?? _failureDetail, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, traceId), CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Diagnostics never change the SCIM answer.
            _logger.LogWarning(exception, "Could not record the SCIM request for diagnostics");
        }
    }

    private IActionResult Refused(ProvisioningTokenCheck check, string scope)
    {
        if (check.Failure == ProvisioningTokenCheck.InsufficientScope)
        {
            Response.Headers.WWWAuthenticate = $"Bearer error=\"insufficient_scope\", scope=\"{scope}\"";
            return Error(StatusCodes.Status403Forbidden, null, $"The provisioning token does not have the {scope} scope.");
        }
        if (check.Failure == ProvisioningTokenCheck.ApplicationInactive)
            return Error(StatusCodes.Status403Forbidden, null, "The provisioning token's application is inactive.");
        Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return Error(StatusCodes.Status401Unauthorized, null, check.Failure switch
        {
            ProvisioningTokenCheck.Revoked => "The provisioning token was revoked.",
            ProvisioningTokenCheck.Expired => "The provisioning token expired.",
            _ => "A valid provisioning token with the required scope is required."
        });
    }

    private IActionResult Resource(string endpoint, OperationResult<ScimResource> result, string? attributes, string? excludedAttributes,
        int status = StatusCodes.Status200OK, bool conditional = false)
    {
        if (!result.IsSuccess)
            return Failure(result.ErrorCode, result.Message);
        var resource = result.Data!;
        Response.Headers.ETag = resource.Version;
        if (conditional && ScimJson.IfNoneMatchNames(Request.Headers.IfNoneMatch.ToString(), resource.Version))
            return StatusCode(StatusCodes.Status304NotModified);
        var location = $"{BaseUrl}/{endpoint}/{resource.Id}";
        if (status == StatusCodes.Status201Created)
            Response.Headers.Location = location;
        return Scim(Present(resource, endpoint, attributes, excludedAttributes), status);
    }

    private IActionResult List(string endpoint, OperationResult<ScimListResult> result, string? attributes, string? excludedAttributes)
    {
        if (!result.IsSuccess)
            return Failure(result.ErrorCode, result.Message);
        var list = result.Data!;
        return Scim(new JsonObject
        {
            ["schemas"] = new JsonArray(ScimDiscovery.ListResponseSchema),
            ["totalResults"] = list.TotalResults,
            ["startIndex"] = list.StartIndex,
            ["itemsPerPage"] = list.Resources.Count,
            ["Resources"] = new JsonArray(list.Resources.Select(resource => (JsonNode)Present(resource, endpoint, attributes, excludedAttributes)).ToArray())
        });
    }

    /// <summary>The resource as the client sees it: with its location and member references, projected.</summary>
    private JsonObject Present(ScimResource resource, string endpoint, string? attributes, string? excludedAttributes)
    {
        var body = (JsonObject)resource.Body.DeepClone();
        if (body["meta"] is JsonObject meta)
            meta["location"] = $"{BaseUrl}/{endpoint}/{resource.Id}";
        if (body["members"] is JsonArray members)
            foreach (var member in members.OfType<JsonObject>())
                member["$ref"] = $"{BaseUrl}/Users/{member["value"]?.GetValue<string>()}";
        return ScimJson.Project(body, attributes, excludedAttributes);
    }

    private IActionResult Deleted(OperationResult result) =>
        result.IsSuccess ? NoContent() : Failure(result.ErrorCode, result.Message);

    private IActionResult Failure(string code, string message) => code switch
    {
        "notFound" => Error(StatusCodes.Status404NotFound, null, message),
        "preconditionFailed" => Error(StatusCodes.Status412PreconditionFailed, null, message),
        "uniqueness" => Error(StatusCodes.Status409Conflict, code, message),
        _ => Error(StatusCodes.Status400BadRequest, code, message)
    };

    private IActionResult Error(int status, string? scimType, string detail)
    {
        _failureType = scimType;
        _failureDetail = detail;
        var error = new JsonObject { ["schemas"] = new JsonArray(ErrorSchema), ["status"] = status.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (scimType is not null)
            error["scimType"] = scimType;
        error["detail"] = detail;
        return Scim(error, status);
    }

    private static JsonObject ListOf(IReadOnlyList<JsonObject> resources) => new()
    {
        ["schemas"] = new JsonArray(ScimDiscovery.ListResponseSchema),
        ["totalResults"] = resources.Count,
        ["startIndex"] = 1,
        ["itemsPerPage"] = resources.Count,
        ["Resources"] = new JsonArray(resources.Select(resource => (JsonNode)resource).ToArray())
    };

    private static int StatusOf(IActionResult result) => result switch
    {
        ContentResult content => content.StatusCode ?? StatusCodes.Status200OK,
        IStatusCodeActionResult withStatus => withStatus.StatusCode ?? StatusCodes.Status200OK,
        _ => StatusCodes.Status200OK
    };

    private static ContentResult Scim(JsonNode value, int status = StatusCodes.Status200OK) =>
        new() { Content = value.ToJsonString(), ContentType = ScimContentType, StatusCode = status };
}
