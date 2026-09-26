using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Profiles;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Services.Scim;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuthCenter.Infrastructure.Services;

/// <summary>
/// SCIM 2.0 Users and Groups of the provisioning token's application (RFC 7643/7644): the directory
/// users with access to it and the directory groups assigned to it. User writes run as one retriable
/// unit, so a refused mapped value leaves nothing half done. Attributes the service does not keep are
/// ignored, unless a profile mapping of the application reads them.
/// </summary>
public sealed partial class ScimService : IScimService
{
    private const string UserType = "User";
    private const string GroupType = "Group";
    private const int MaxOperations = 100;
    private const int MaxNameLength = 200;
    private const int MaxExternalIdLength = 300;

    private readonly AuthCenterDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IUserAccessService _access;
    private readonly IUserProfileService _profiles;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly DynamicGroupMembershipService _dynamicGroups;

    public ScimService(
        AuthCenterDbContext db,
        UserManager<ApplicationUser> users,
        IUserAccessService access,
        IUserProfileService profiles,
        IRefreshTokenService refreshTokens,
        IDateTimeProvider clock,
        IAuditService audit,
        DynamicGroupMembershipService dynamicGroups)
    {
        _db = db;
        _users = users;
        _access = access;
        _profiles = profiles;
        _refreshTokens = refreshTokens;
        _clock = clock;
        _audit = audit;
        _dynamicGroups = dynamicGroups;
    }

    // Users

    public async Task<OperationResult<ScimListResult>> ListUsersAsync(ProvisioningPrincipal principal, ScimListRequest request, CancellationToken ct = default)
    {
        var appId = principal.ApplicationSystemId;
        var rows = UserRows(appId);
        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            if (!TryParseFilter(request.Filter, out var attribute, out var value))
                return ListFailure("invalidFilter", "Filters take the form: attribute eq \"value\".");
            switch (attribute.ToLowerInvariant())
            {
                case "username" or "emails" or "emails.value":
                    var normalized = _users.NormalizeEmail(value);
                    rows = rows.Where(row => row.User.NormalizedEmail == normalized);
                    break;
                case "externalid":
                    rows = rows.Where(row => row.Link != null && row.Link.ExternalId == value);
                    break;
                case "id":
                    var id = Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;
                    rows = rows.Where(row => row.User.Id == id);
                    break;
                default:
                    return ListFailure("invalidFilter", "Users can be filtered by userName, emails, externalId or id.");
            }
        }

        var descending = IsDescending(request.SortOrder);
        IOrderedQueryable<UserRow>? sorted = request.SortBy?.Trim().ToLowerInvariant() switch
        {
            null or "" or "username" or "emails" or "emails.value" => Order(rows, row => row.User.Email, descending),
            "displayname" or "name.formatted" => Order(rows, row => row.User.FullName, descending),
            "externalid" => Order(rows, row => row.Link == null ? null : row.Link.ExternalId, descending),
            "id" => Order(rows, row => row.User.Id, descending),
            "meta.created" => Order(rows, row => row.User.CreatedAt, descending),
            "meta.lastmodified" => Order(rows, row => row.User.UpdatedAt ?? row.User.CreatedAt, descending),
            _ => null
        };
        if (sorted is null)
            return ListFailure("invalidValue", "Users can be sorted by userName, displayName, externalId, id, meta.created or meta.lastModified.");

        var (start, count) = Page(request);
        var total = await rows.CountAsync(ct);
        var page = count == 0 ? [] : await sorted.ThenBy(row => row.User.Id).Skip(start - 1).Take(count).ToListAsync(ct);
        var mappings = await MappingsAsync(appId, ct);
        return OperationResult<ScimListResult>.Success(new ScimListResult(total, start, await UserResourcesAsync(page, mappings, ct)));
    }

    public async Task<OperationResult<ScimResource>> GetUserAsync(ProvisioningPrincipal principal, Guid id, CancellationToken ct = default)
    {
        var row = await UserRows(principal.ApplicationSystemId).SingleOrDefaultAsync(candidate => candidate.User.Id == id, ct);
        return row is null
            ? UserNotFound()
            : OperationResult<ScimResource>.Success((await UserResourcesAsync([row], await MappingsAsync(principal.ApplicationSystemId, ct), ct))[0]);
    }

    public async Task<OperationResult<ScimResource>> CreateUserAsync(ProvisioningPrincipal principal, JsonElement payload, CancellationToken ct = default)
    {
        var input = ReadUser(payload);
        if (!input.IsSuccess)
            return Failure<ScimResource>(input.ErrorCode, input.Message);
        var user = input.Data!;
        var appId = principal.ApplicationSystemId;

        var created = await _db.RunRetriableAsync(async () =>
        {
            await using var transaction = await BeginAsync(ct);
            if (await _users.FindByEmailAsync(user.UserName) is not null)
                return Failure<Guid>("uniqueness", "userName already exists.");
            var now = _clock.UtcNow;
            var entity = new ApplicationUser
            {
                Id = Guid.NewGuid(), Email = user.UserName, UserName = user.UserName, FullName = user.FullName ?? user.UserName,
                EmailConfirmed = true, IsExternalUser = true, HasLocalPassword = false, IsActive = user.Active ?? true, CreatedAt = now
            };
            var result = await _users.CreateAsync(entity);
            if (!result.Succeeded)
                return Failure<Guid>("invalidValue", string.Join("; ", result.Errors.Select(error => error.Description)));
            // The identity provider decides: an inactive user's access is revoked, never pending approval.
            var access = await _access.GrantAccessAsync(entity.Id, appId, true, ct);
            if (!access.IsSuccess)
                return Failure<Guid>("invalidValue", access.Message);
            if (!entity.IsActive)
            {
                var granted = await _db.UserApplicationAccesses.SingleAsync(item => item.UserId == entity.Id && item.ApplicationSystemId == appId, ct);
                granted.IsActive = false;
                granted.RevokedAt = now;
            }
            _db.ScimResourceLinks.Add(new ScimResourceLink { Id = Guid.NewGuid(), ApplicationSystemId = appId, ResourceType = UserType, ResourceId = entity.Id, ExternalId = user.ExternalId, CreatedAt = now, UpdatedAt = now });
            await _db.SaveChangesAsync(ct);
            var mapped = await ApplyMappedValuesAsync(entity.Id, MappedValues(payload, await MappingsAsync(appId, ct), clearMissing: false), ct);
            if (mapped is not null)
                return Failure<Guid>("invalidValue", mapped);
            await CommitAsync(transaction, ct);
            return OperationResult<Guid>.Success(entity.Id);
        });
        if (!created.IsSuccess)
            return Failure<ScimResource>(created.ErrorCode, created.Message);
        await _audit.LogAsync("SCIM_USER_CREATED", created.Data, entityName: nameof(ApplicationUser), entityId: created.Data.ToString(), metadata: new { principal.ApplicationSystemId, principal.TokenId }, ct: ct);
        return await GetUserAsync(principal, created.Data, ct);
    }

    public async Task<OperationResult<ScimResource>> ReplaceUserAsync(ProvisioningPrincipal principal, Guid id, JsonElement payload, string? ifMatch, CancellationToken ct = default)
    {
        var input = ReadUser(payload);
        if (!input.IsSuccess)
            return Failure<ScimResource>(input.ErrorCode, input.Message);
        var replacement = input.Data!;
        return await WriteUserAsync(principal, id, ifMatch, "SCIM_USER_UPDATED", (mappings, changes) =>
        {
            // PUT replaces: what the request leaves out is cleared, except active, which a client that
            // omits it does not mean to change.
            changes.UserName = replacement.UserName;
            changes.FullName = replacement.FullName ?? replacement.UserName;
            changes.Active = replacement.Active;
            changes.ExternalIdChanged = true;
            changes.ExternalId = replacement.ExternalId;
            foreach (var (definitionId, value) in MappedValues(payload, mappings, clearMissing: true))
                changes.Mapped[definitionId] = value;
            return null;
        }, ct);
    }

    public async Task<OperationResult<ScimResource>> PatchUserAsync(ProvisioningPrincipal principal, Guid id, JsonElement payload, string? ifMatch, CancellationToken ct = default)
    {
        var operations = ReadOperations(payload);
        if (!operations.IsSuccess)
            return Failure<ScimResource>(operations.ErrorCode, operations.Message);
        return await WriteUserAsync(principal, id, ifMatch, "SCIM_USER_UPDATED", (mappings, changes) =>
        {
            foreach (var operation in operations.Data!)
            {
                var error = operation.Path is null
                    ? ApplyUserObject(operation.Value, changes, mappings)
                    : operation.Op == "remove"
                        ? RemoveUserValue(operation.Path, changes, mappings)
                        : SetUserValue(operation.Path, operation.Value, operation.Op == "add", changes, mappings);
                if (error is not null)
                    return error;
            }
            return null;
        }, ct);
    }

    public async Task<OperationResult> DeleteUserAsync(ProvisioningPrincipal principal, Guid id, string? ifMatch, CancellationToken ct = default)
    {
        var result = await WriteUserAsync(principal, id, ifMatch, "SCIM_USER_DEPROVISIONED", (_, changes) =>
        {
            // Deprovisioning keeps the identity and its history.
            changes.Active = false;
            return null;
        }, ct);
        return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(result.ErrorCode, result.Message);
    }

    /// <summary>Loads the user, checks the precondition, collects the changes and saves them as one unit.</summary>
    private async Task<OperationResult<ScimResource>> WriteUserAsync(
        ProvisioningPrincipal principal, Guid id, string? ifMatch, string auditAction,
        Func<IReadOnlyList<ScimMapping>, UserChanges, (string Code, string Message)?> collect, CancellationToken ct)
    {
        var appId = principal.ApplicationSystemId;
        var written = await _db.RunRetriableAsync(async () =>
        {
            await using var transaction = await BeginAsync(ct);
            var row = await UserRows(appId, tracked: true).SingleOrDefaultAsync(candidate => candidate.User.Id == id, ct);
            if (row is null)
                return Failure<bool>("notFound", "SCIM user not found.");
            var mappings = await MappingsAsync(appId, ct);
            var current = (await UserResourcesAsync([row], mappings, ct))[0];
            if (!ScimJson.IfMatchAllows(ifMatch, current.Version))
                return Failure<bool>("preconditionFailed", "The user changed since the version in If-Match.");

            var changes = new UserChanges();
            if (collect(mappings, changes) is { } error)
                return Failure<bool>(error.Code, error.Message);
            var user = row.User;
            var wasActive = user.IsActive;
            if (changes.UserName is { } userName && !string.Equals(userName, user.Email, StringComparison.OrdinalIgnoreCase))
            {
                var other = await _users.FindByEmailAsync(userName);
                if (other is not null && other.Id != user.Id)
                    return Failure<bool>("uniqueness", "userName already exists.");
            }
            if (changes.UserName is { } newUserName)
            {
                user.Email = newUserName;
                user.UserName = newUserName;
            }
            if (changes.ComposedName() is { } fullName)
                user.FullName = fullName;
            if (changes.Active is { } active)
                user.IsActive = active;
            var now = _clock.UtcNow;
            user.UpdatedAt = now;
            var updated = await _users.UpdateAsync(user);
            if (!updated.Succeeded)
                return Failure<bool>("invalidValue", string.Join("; ", updated.Errors.Select(item => item.Description)));

            if (changes.ExternalIdChanged && !string.Equals(row.Link?.ExternalId, changes.ExternalId, StringComparison.Ordinal))
            {
                if (row.Link is { } link)
                {
                    link.ExternalId = changes.ExternalId;
                    link.UpdatedAt = now;
                }
                else
                {
                    _db.ScimResourceLinks.Add(new ScimResourceLink { Id = Guid.NewGuid(), ApplicationSystemId = appId, ResourceType = UserType, ResourceId = user.Id, ExternalId = changes.ExternalId, CreatedAt = now, UpdatedAt = now });
                }
            }
            var access = await _db.UserApplicationAccesses.SingleAsync(item => item.UserId == user.Id && item.ApplicationSystemId == appId, ct);
            access.IsActive = user.IsActive;
            // Deprovisioned is revoked, not pending approval.
            access.RevokedAt = user.IsActive ? null : access.RevokedAt ?? now;
            await _db.SaveChangesAsync(ct);
            if (wasActive && !user.IsActive)
                await _refreshTokens.RevokeAllForUserAsync(user.Id, ct);
            if (await ApplyMappedValuesAsync(user.Id, changes.Mapped, ct) is { } mappedError)
                return Failure<bool>("invalidValue", mappedError);
            await CommitAsync(transaction, ct);
            return OperationResult<bool>.Success(user.IsActive);
        });
        if (!written.IsSuccess)
            return Failure<ScimResource>(written.ErrorCode, written.Message);
        var action = written.Data || auditAction == "SCIM_USER_DEPROVISIONED" ? auditAction : "SCIM_USER_DEPROVISIONED";
        await _audit.LogAsync(action, id, entityName: nameof(ApplicationUser), entityId: id.ToString(), metadata: new { principal.ApplicationSystemId, principal.TokenId }, ct: ct);
        return await GetUserAsync(principal, id, ct);
    }

    /// <summary>A PATCH operation without a path: its value holds attributes (Entra ID and Okta send these).</summary>
    private static (string Code, string Message)? ApplyUserObject(JsonElement value, UserChanges changes, IReadOnlyList<ScimMapping> mappings)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (IsSchemaContainer(property.Name, mappings) && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var extension in property.Value.EnumerateObject())
                {
                    if (!ScimPath.TryParse($"{property.Name}:{extension.Name}", out var extensionPath))
                        return ("invalidPath", $"{property.Name}:{extension.Name} is not a valid attribute path.");
                    if (SetUserValue(extensionPath, extension.Value, false, changes, mappings) is { } error)
                        return error;
                }
                continue;
            }
            if (!ScimPath.TryParse(property.Name, out var path))
                return ("invalidPath", $"{property.Name} is not a valid attribute path.");
            if (SetUserValue(path, property.Value, false, changes, mappings) is { } failure)
                return failure;
        }
        return null;
    }

    private static (string Code, string Message)? SetUserValue(ScimPath path, JsonElement value, bool merge, UserChanges changes, IReadOnlyList<ScimMapping> mappings)
    {
        if (path.Schema is null && path.Filter is null)
        {
            switch (path.Attribute.ToLowerInvariant())
            {
                case "username" when path.SubAttribute is null:
                    if (!TryText(value, out var userName) || userName.Length > 256)
                        return ("invalidValue", "userName must be a bounded email address.");
                    changes.UserName = userName;
                    break;
                case "displayname" when path.SubAttribute is null:
                    if (!TryText(value, out var displayName))
                        return ("invalidValue", "displayName must be text.");
                    changes.DisplayName = displayName;
                    break;
                case "name" when path.SubAttribute is null:
                    if (value.ValueKind != JsonValueKind.Object)
                        return ("invalidValue", "name must be an object.");
                    foreach (var part in value.EnumerateObject())
                        changes.SetNamePart(part.Name, part.Value);
                    break;
                case "name":
                    changes.SetNamePart(path.SubAttribute!, value);
                    break;
                case "active" when path.SubAttribute is null:
                    if (!TryBoolean(value, out var active))
                        return ("invalidValue", "active must be true or false.");
                    changes.Active = active;
                    break;
                case "externalid" when path.SubAttribute is null:
                    if (value.ValueKind == JsonValueKind.Null)
                        changes.SetExternalId(null);
                    else if (TryText(value, out var externalId) && externalId.Length <= MaxExternalIdLength)
                        changes.SetExternalId(externalId);
                    else
                        return ("invalidValue", $"externalId must be text of at most {MaxExternalIdLength} characters.");
                    break;
            }
        }
        MapValue(path, value, merge, changes.Mapped, mappings);
        return null;
    }

    private static (string Code, string Message)? RemoveUserValue(ScimPath path, UserChanges changes, IReadOnlyList<ScimMapping> mappings)
    {
        if (path.Schema is null && path.Filter is null && path.SubAttribute is null)
        {
            switch (path.Attribute.ToLowerInvariant())
            {
                case "username":
                    return ("mutability", "userName is required and cannot be removed.");
                case "active":
                    changes.Active = false;
                    break;
                case "externalid":
                    changes.SetExternalId(null);
                    break;
            }
        }
        foreach (var mapping in mappings.Where(mapping => mapping.Path.IsEquivalentTo(path) || Contains(path, mapping.Path)))
            changes.Mapped[mapping.DefinitionId] = null;
        return null;
    }

    /// <summary>
    /// The mapped attributes an operation sets: the one at its path, or those inside the complex or
    /// multi-valued attribute it sets as a whole (<c>name</c>, <c>emails</c>, an extension).
    /// </summary>
    private static void MapValue(ScimPath path, JsonElement value, bool merge, Dictionary<Guid, JsonElement?> mapped, IReadOnlyList<ScimMapping> mappings)
    {
        foreach (var mapping in mappings)
        {
            if (mapping.Path.IsEquivalentTo(path))
            {
                mapped[mapping.DefinitionId] = value.ValueKind == JsonValueKind.Null ? null : value.Clone();
                continue;
            }
            if (!Contains(path, mapping.Path))
                continue;
            if (mapping.Path.TryResolve(Wrap(path, value), out var inner))
                mapped[mapping.DefinitionId] = inner.ValueKind == JsonValueKind.Null ? null : inner.Clone();
            else if (!merge && value.ValueKind == JsonValueKind.Array)
                // Replacing a multi-valued attribute drops the values it no longer has; a complex
                // attribute keeps the sub-attributes the operation leaves out (RFC 7644 §3.5.2.3).
                mapped[mapping.DefinitionId] = null;
        }
    }

    /// <summary>Whether <paramref name="inner"/> lies within the attribute <paramref name="outer"/> names as a whole.</summary>
    private static bool Contains(ScimPath outer, ScimPath inner) =>
        outer.SubAttribute is null &&
        string.Equals(outer.Schema, inner.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(outer.Attribute, inner.Attribute, StringComparison.OrdinalIgnoreCase) &&
        (inner.SubAttribute is not null || inner.Filter is not null) &&
        (outer.Filter is null || inner.Filter is not null && outer.Filter.SameAs(inner.Filter));

    /// <summary>A document holding <paramref name="value"/> where <paramref name="path"/> points.</summary>
    private static JsonElement Wrap(ScimPath path, JsonElement value)
    {
        JsonNode? node = ScimJson.Node(value);
        if (path.Filter is not null && node is JsonObject element)
        {
            element[path.Filter.Attribute] = ScimJson.Node(path.Filter.Value);
            node = new JsonArray(element);
        }
        var container = new JsonObject { [path.Attribute] = node };
        var document = path.Schema is null ? container : new JsonObject { [path.Schema] = container };
        return JsonSerializer.SerializeToElement(document);
    }

    private static Dictionary<Guid, JsonElement?> MappedValues(JsonElement payload, IReadOnlyList<ScimMapping> mappings, bool clearMissing)
    {
        var values = new Dictionary<Guid, JsonElement?>();
        foreach (var mapping in mappings)
        {
            if (mapping.Path.TryResolve(payload, out var value))
                values[mapping.DefinitionId] = value.ValueKind == JsonValueKind.Null ? null : value.Clone();
            else if (clearMissing)
                values[mapping.DefinitionId] = null;
        }
        return values;
    }

    private async Task<string?> ApplyMappedValuesAsync(Guid userId, IReadOnlyDictionary<Guid, JsonElement?> values, CancellationToken ct)
    {
        if (values.Count == 0)
            return null;
        var definitionIds = values.Keys.ToList();
        var keys = await _db.UserProfileAttributeDefinitions.AsNoTracking()
            .Where(definition => definitionIds.Contains(definition.Id))
            .ToDictionaryAsync(definition => definition.Id, definition => definition.Key, ct);
        var attributes = values.Where(pair => keys.ContainsKey(pair.Key))
            .ToDictionary(pair => keys[pair.Key], pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        // The profile service validates each value against its attribute and re-evaluates the user's
        // rule-managed groups.
        var result = await _profiles.UpdateUserProfileFromSourceAsync(userId, new UpdateUserProfileRequest { Attributes = attributes }, "SCIM", ct);
        return result.IsSuccess ? null : result.Message;
    }

    private IQueryable<UserRow> UserRows(Guid appId, bool tracked = false)
    {
        var users = tracked ? _db.Users.IgnoreQueryFilters() : _db.Users.IgnoreQueryFilters().AsNoTracking();
        var links = tracked ? _db.ScimResourceLinks : _db.ScimResourceLinks.AsNoTracking();
        // Tracking is decided for the whole query: every source follows the same choice.
        var accesses = tracked ? _db.UserApplicationAccesses : _db.UserApplicationAccesses.AsNoTracking();
        return from access in accesses
               where access.ApplicationSystemId == appId
               join user in users on access.UserId equals user.Id
               join link in links.Where(link => link.ApplicationSystemId == appId && link.ResourceType == UserType) on user.Id equals link.ResourceId into userLinks
               from link in userLinks.DefaultIfEmpty()
               select new UserRow { User = user, Link = link };
    }

    private async Task<List<ScimResource>> UserResourcesAsync(IReadOnlyList<UserRow> rows, IReadOnlyList<ScimMapping> mappings, CancellationToken ct)
    {
        var ids = rows.Select(row => row.User.Id).ToList();
        var definitionIds = mappings.Select(mapping => mapping.DefinitionId).Distinct().ToList();
        var values = ids.Count == 0 || definitionIds.Count == 0
            ? []
            : await _db.UserProfileAttributeValues.AsNoTracking()
                .Where(value => ids.Contains(value.UserId) && definitionIds.Contains(value.AttributeDefinitionId))
                .Select(value => new { value.UserId, value.AttributeDefinitionId, value.ValueJson })
                .ToListAsync(ct);
        return rows.Select(row => UserResource(row, mappings, values
            .Where(value => value.UserId == row.User.Id)
            .ToDictionary(value => value.AttributeDefinitionId, value => value.ValueJson))).ToList();
    }

    private static ScimResource UserResource(UserRow row, IReadOnlyList<ScimMapping> mappings, IReadOnlyDictionary<Guid, string> values)
    {
        var user = row.User;
        var body = new JsonObject
        {
            ["schemas"] = new JsonArray(ScimPath.CoreUserSchema),
            ["id"] = user.Id.ToString()
        };
        if (row.Link?.ExternalId is { } externalId)
            body["externalId"] = externalId;
        body["userName"] = user.Email;
        body["name"] = new JsonObject { ["formatted"] = user.FullName };
        body["displayName"] = user.FullName;
        body["emails"] = new JsonArray(new JsonObject { ["value"] = user.Email, ["type"] = "work", ["primary"] = true });
        body["active"] = user.IsActive;
        // Mapped attributes are returned where the client sent them, next to the core ones.
        foreach (var mapping in mappings.OrderBy(mapping => mapping.Path.Canonical, StringComparer.OrdinalIgnoreCase))
            if (mapping.Path.Filter is null && !IsCoreUserAttribute(mapping.Path) && values.TryGetValue(mapping.DefinitionId, out var json))
                ScimJson.Set(body, mapping.Path, ScimJson.Node(json));
        var version = ScimJson.Version(body);
        body["meta"] = new JsonObject
        {
            ["resourceType"] = UserType,
            ["created"] = ScimJson.Timestamp(user.CreatedAt),
            ["lastModified"] = ScimJson.Timestamp(user.UpdatedAt ?? user.CreatedAt),
            ["version"] = version
        };
        return new ScimResource(user.Id, body, version);
    }

    private static bool IsCoreUserAttribute(ScimPath path) =>
        path.Schema is null && (path.Attribute.ToLowerInvariant() is "id" or "schemas" or "meta" or "username" or "displayname" or "emails" or "active" or "externalid" or "groups" ||
            path.Attribute.Equals("name", StringComparison.OrdinalIgnoreCase) && (path.SubAttribute is null || path.SubAttribute.Equals("formatted", StringComparison.OrdinalIgnoreCase)));

    private static OperationResult<UserInput> ReadUser(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return OperationResult<UserInput>.Failure("invalidSyntax", "The request body must be a SCIM User.");
        if (!ScimPath.TryProperty(payload, "userName", out var userNameElement) || !TryText(userNameElement, out var userName) || userName.Length > 256)
            return OperationResult<UserInput>.Failure("invalidValue", "userName must be a bounded email address.");
        var changes = new UserChanges();
        if (ScimPath.TryProperty(payload, "name", out var name) && name.ValueKind == JsonValueKind.Object)
            foreach (var part in name.EnumerateObject())
                changes.SetNamePart(part.Name, part.Value);
        if (ScimPath.TryProperty(payload, "displayName", out var displayName) && TryText(displayName, out var display))
            changes.DisplayName = display;
        bool? active = null;
        if (ScimPath.TryProperty(payload, "active", out var activeElement))
        {
            if (!TryBoolean(activeElement, out var parsedActive))
                return OperationResult<UserInput>.Failure("invalidValue", "active must be true or false.");
            active = parsedActive;
        }
        string? externalId = null;
        if (ScimPath.TryProperty(payload, "externalId", out var externalIdElement) && externalIdElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryText(externalIdElement, out var text) || text.Length > MaxExternalIdLength)
                return OperationResult<UserInput>.Failure("invalidValue", $"externalId must be text of at most {MaxExternalIdLength} characters.");
            externalId = text;
        }
        return OperationResult<UserInput>.Success(new UserInput(userName, changes.ComposedName(), active, externalId));
    }

    // Shared

    private static OperationResult<IReadOnlyList<PatchOperation>> ReadOperations(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !ScimPath.TryProperty(payload, "Operations", out var operations) ||
            operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() is 0 or > MaxOperations)
            return OperationResult<IReadOnlyList<PatchOperation>>.Failure("invalidSyntax", $"PATCH requires 1-{MaxOperations} Operations.");
        var result = new List<PatchOperation>();
        foreach (var operation in operations.EnumerateArray())
        {
            var op = ScimPath.TryProperty(operation, "op", out var opElement) && opElement.ValueKind == JsonValueKind.String ? opElement.GetString()!.ToLowerInvariant() : null;
            if (op is not ("add" or "replace" or "remove"))
                return OperationResult<IReadOnlyList<PatchOperation>>.Failure("invalidSyntax", "Each operation needs op add, replace or remove.");
            ScimPath? path = null;
            if (ScimPath.TryProperty(operation, "path", out var pathElement) && pathElement.ValueKind != JsonValueKind.Null)
            {
                if (pathElement.ValueKind != JsonValueKind.String || !ScimPath.TryParse(pathElement.GetString(), out var parsed))
                    return OperationResult<IReadOnlyList<PatchOperation>>.Failure("invalidPath", $"{pathElement.GetRawText()} is not a valid attribute path.");
                path = parsed;
            }
            var hasValue = ScimPath.TryProperty(operation, "value", out var value);
            if (path is null && op == "remove")
                return OperationResult<IReadOnlyList<PatchOperation>>.Failure("noTarget", "A remove operation needs a path.");
            if (path is null && (!hasValue || value.ValueKind != JsonValueKind.Object))
                return OperationResult<IReadOnlyList<PatchOperation>>.Failure("invalidSyntax", "An operation without a path needs an object value.");
            if (op != "remove" && !hasValue)
                return OperationResult<IReadOnlyList<PatchOperation>>.Failure("invalidSyntax", $"The {op} operation needs a value.");
            result.Add(new PatchOperation(op, path, hasValue ? value.Clone() : default));
        }
        return OperationResult<IReadOnlyList<PatchOperation>>.Success(result);
    }

    private async Task<IReadOnlyList<ScimMapping>> MappingsAsync(Guid appId, CancellationToken ct)
    {
        var mappings = await _db.ProfileMappings.AsNoTracking()
            .Where(mapping => mapping.ApplicationSystemId == appId && mapping.SourceSystem == "SCIM" && mapping.IsActive && mapping.TargetAttributeDefinition.IsActive)
            .Select(mapping => new { mapping.SourcePath, mapping.TargetAttributeDefinitionId })
            .ToListAsync(ct);
        return mappings
            .Select(mapping => ScimPath.TryParse(mapping.SourcePath, out var path) ? new ScimMapping(path, mapping.TargetAttributeDefinitionId) : null)
            .OfType<ScimMapping>()
            .ToList();
    }

    private async Task<IDbContextTransaction?> BeginAsync(CancellationToken ct) =>
        _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;

    private static async Task CommitAsync(IDbContextTransaction? transaction, CancellationToken ct)
    {
        if (transaction is not null)
            await transaction.CommitAsync(ct);
    }

    private static bool TryParseFilter(string filter, out string attribute, out string value)
    {
        var match = FilterPattern().Match(filter.Trim());
        attribute = match.Success ? match.Groups["attribute"].Value : string.Empty;
        value = match.Success ? match.Groups["value"].Value : string.Empty;
        return match.Success;
    }

    /// <summary>Whether a key of a PATCH value is a schema holding attributes (<c>{"urn:…:User": {"department": …}}</c>).</summary>
    private static bool IsSchemaContainer(string name, IReadOnlyList<ScimMapping> mappings) =>
        name.Equals(ScimPath.EnterpriseUserSchema, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(ScimPath.CoreUserSchema, StringComparison.OrdinalIgnoreCase) ||
        mappings.Any(mapping => string.Equals(mapping.Path.Schema, name, StringComparison.OrdinalIgnoreCase));

    private static bool TryText(JsonElement value, out string text)
    {
        text = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : string.Empty;
        return text.Length > 0;
    }

    /// <summary>A boolean, or its text: Entra ID sends <c>"False"</c> for active.</summary>
    private static bool TryBoolean(JsonElement value, out bool result)
    {
        result = value.ValueKind == JsonValueKind.True;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return true;
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out result);
    }

    private static (int Start, int Count) Page(ScimListRequest request) =>
        (Math.Max(request.StartIndex, 1), Math.Clamp(request.Count, 0, ScimDiscovery.MaxResults));

    private static bool IsDescending(string? sortOrder) => string.Equals(sortOrder?.Trim(), "descending", StringComparison.OrdinalIgnoreCase);

    private static IOrderedQueryable<T> Order<T, TKey>(IQueryable<T> rows, Expression<Func<T, TKey>> key, bool descending) =>
        descending ? rows.OrderByDescending(key) : rows.OrderBy(key);

    private static OperationResult<T> Failure<T>(string code, string message) => OperationResult<T>.Failure(code, message);

    private static OperationResult<ScimListResult> ListFailure(string code, string message) => OperationResult<ScimListResult>.Failure(code, message);

    private static OperationResult<ScimResource> UserNotFound() => OperationResult<ScimResource>.Failure("notFound", "SCIM user not found.");

    [GeneratedRegex("""^(?<attribute>[A-Za-z][\w.:$-]*)\s+eq\s+"(?<value>[^"]{1,300})"$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex FilterPattern();

    private sealed class UserRow
    {
        public ApplicationUser User { get; init; } = null!;
        public ScimResourceLink? Link { get; init; }
    }

    private sealed record UserInput(string UserName, string? FullName, bool? Active, string? ExternalId);

    private sealed record ScimMapping(ScimPath Path, Guid DefinitionId);

    private sealed record PatchOperation(string Op, ScimPath? Path, JsonElement Value);

    private sealed class UserChanges
    {
        public string? UserName { get; set; }
        public string? DisplayName { get; set; }
        public string? FullName { get; set; }
        public string? Formatted { get; private set; }
        public string? GivenName { get; private set; }
        public string? FamilyName { get; private set; }
        public bool? Active { get; set; }
        public bool ExternalIdChanged { get; set; }
        public string? ExternalId { get; set; }
        public Dictionary<Guid, JsonElement?> Mapped { get; } = [];

        public void SetExternalId(string? value)
        {
            ExternalIdChanged = true;
            ExternalId = value;
        }

        public void SetNamePart(string part, JsonElement value)
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : null;
            switch (part.ToLowerInvariant())
            {
                case "formatted": Formatted = text; break;
                case "givenname": GivenName = text; break;
                case "familyname": FamilyName = text; break;
            }
        }

        /// <summary>
        /// The full name: explicit, the display name, the formatted name, or given and family names
        /// together (one of them alone would lose the other).
        /// </summary>
        public string? ComposedName()
        {
            var name = FullName ?? DisplayName ?? Formatted ??
                (GivenName is { Length: > 0 } && FamilyName is { Length: > 0 } ? $"{GivenName} {FamilyName}" : null);
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, MaxNameLength)];
        }
    }
}
