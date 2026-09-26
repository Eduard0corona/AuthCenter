using System.Text.Json;
using System.Text.Json.Nodes;
using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Services.Scim;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public sealed partial class ScimService
{
    public async Task<OperationResult<ScimListResult>> ListGroupsAsync(ProvisioningPrincipal principal, ScimListRequest request, CancellationToken ct = default)
    {
        var rows = GroupRows(principal.ApplicationSystemId);
        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            if (!TryParseFilter(request.Filter, out var attribute, out var value))
                return ListFailure("invalidFilter", "Filters take the form: attribute eq \"value\".");
            switch (attribute.ToLowerInvariant())
            {
                case "displayname":
                    var normalized = NormalizeGroupName(value);
                    rows = rows.Where(row => row.Group.NormalizedName == normalized);
                    break;
                case "externalid":
                    rows = rows.Where(row => row.Link != null && row.Link.ExternalId == value);
                    break;
                case "id":
                    var id = Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;
                    rows = rows.Where(row => row.Group.Id == id);
                    break;
                default:
                    return ListFailure("invalidFilter", "Groups can be filtered by displayName, externalId or id.");
            }
        }

        var descending = IsDescending(request.SortOrder);
        IOrderedQueryable<GroupRow>? sorted = request.SortBy?.Trim().ToLowerInvariant() switch
        {
            null or "" or "displayname" => Order(rows, row => row.Group.Name, descending),
            "externalid" => Order(rows, row => row.Link == null ? null : row.Link.ExternalId, descending),
            "id" => Order(rows, row => row.Group.Id, descending),
            "meta.created" => Order(rows, row => row.Group.CreatedAt, descending),
            "meta.lastmodified" => Order(rows, row => row.Group.UpdatedAt ?? row.Group.CreatedAt, descending),
            _ => null
        };
        if (sorted is null)
            return ListFailure("invalidValue", "Groups can be sorted by displayName, externalId, id, meta.created or meta.lastModified.");

        var (start, count) = Page(request);
        var total = await rows.CountAsync(ct);
        var page = count == 0 ? [] : await sorted.ThenBy(row => row.Group.Id).Skip(start - 1).Take(count).ToListAsync(ct);
        return OperationResult<ScimListResult>.Success(new ScimListResult(total, start, await GroupResourcesAsync(page, ct)));
    }

    public async Task<OperationResult<ScimResource>> GetGroupAsync(ProvisioningPrincipal principal, Guid id, CancellationToken ct = default)
    {
        var row = await GroupRows(principal.ApplicationSystemId).SingleOrDefaultAsync(candidate => candidate.Group.Id == id, ct);
        return row is null ? GroupNotFound() : OperationResult<ScimResource>.Success((await GroupResourcesAsync([row], ct))[0]);
    }

    public async Task<OperationResult<ScimResource>> CreateGroupAsync(ProvisioningPrincipal principal, JsonElement payload, CancellationToken ct = default)
    {
        var input = ReadGroup(payload);
        if (!input.IsSuccess)
            return Failure<ScimResource>(input.ErrorCode, input.Message);
        var group = input.Data!;
        var appId = principal.ApplicationSystemId;
        var normalized = NormalizeGroupName(group.DisplayName);
        if (await _db.DirectoryGroups.AnyAsync(item => item.NormalizedName == normalized, ct))
            return Failure<ScimResource>("uniqueness", "displayName already exists.");
        var members = group.Members ?? [];
        if (await ForeignMemberAsync(appId, members, ct) is { } foreign)
            return Failure<ScimResource>("invalidValue", $"Member {foreign} is not a user of this application.");

        var now = _clock.UtcNow;
        var entity = new DirectoryGroup { Id = Guid.NewGuid(), Name = group.DisplayName, NormalizedName = normalized, IsActive = true, CreatedAt = now };
        _db.DirectoryGroups.Add(entity);
        _db.GroupApplicationAssignments.Add(new GroupApplicationAssignment { GroupId = entity.Id, ApplicationSystemId = appId, CreatedAt = now });
        _db.ScimResourceLinks.Add(new ScimResourceLink { Id = Guid.NewGuid(), ApplicationSystemId = appId, ResourceType = GroupType, ResourceId = entity.Id, ExternalId = group.ExternalId, CreatedAt = now, UpdatedAt = now });
        foreach (var userId in members)
            _db.UserGroupMemberships.Add(new UserGroupMembership { GroupId = entity.Id, UserId = userId, CreatedAt = now });
        await RevokeGroupSessionsAsync(entity.Id, members, [appId], ct);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("SCIM_GROUP_CREATED", entityName: nameof(DirectoryGroup), entityId: entity.Id.ToString(), metadata: new { principal.ApplicationSystemId, principal.TokenId, members = members.Count }, ct: ct);
        return await GetGroupAsync(principal, entity.Id, ct);
    }

    public async Task<OperationResult<ScimResource>> ReplaceGroupAsync(ProvisioningPrincipal principal, Guid id, JsonElement payload, string? ifMatch, CancellationToken ct = default)
    {
        var input = ReadGroup(payload);
        if (!input.IsSuccess)
            return Failure<ScimResource>(input.ErrorCode, input.Message);
        var replacement = input.Data!;
        return await WriteGroupAsync(principal, id, ifMatch, (_, changes) =>
        {
            changes.DisplayName = replacement.DisplayName;
            changes.SetExternalId(replacement.ExternalId);
            changes.Members = replacement.Members?.ToHashSet() ?? [];
            return null;
        }, ct);
    }

    public async Task<OperationResult<ScimResource>> PatchGroupAsync(ProvisioningPrincipal principal, Guid id, JsonElement payload, string? ifMatch, CancellationToken ct = default)
    {
        var operations = ReadOperations(payload);
        if (!operations.IsSuccess)
            return Failure<ScimResource>(operations.ErrorCode, operations.Message);
        return await WriteGroupAsync(principal, id, ifMatch, (members, changes) =>
        {
            changes.Members = members.ToHashSet();
            foreach (var operation in operations.Data!)
            {
                var error = operation.Path is null
                    ? ApplyGroupObject(operation.Value, changes)
                    : ApplyGroupOperation(operation.Op, operation.Path, operation.Value, changes);
                if (error is not null)
                    return error;
            }
            return null;
        }, ct);
    }

    public async Task<OperationResult> DeleteGroupAsync(ProvisioningPrincipal principal, Guid id, string? ifMatch, CancellationToken ct = default)
    {
        var appId = principal.ApplicationSystemId;
        var row = await GroupRows(appId, tracked: true).SingleOrDefaultAsync(candidate => candidate.Group.Id == id, ct);
        if (row is null)
            return OperationResult.Failure("notFound", "SCIM group not found.");
        var current = (await GroupResourcesAsync([row], ct))[0];
        if (!ScimJson.IfMatchAllows(ifMatch, current.Version))
            return OperationResult.Failure("preconditionFailed", "The group changed since the version in If-Match.");
        // Deprovisioning removes the group from the application and deactivates it; it is not erased.
        row.Group.IsActive = false;
        row.Group.UpdatedAt = _clock.UtcNow;
        _db.GroupApplicationAssignments.RemoveRange(await _db.GroupApplicationAssignments.Where(item => item.GroupId == id && item.ApplicationSystemId == appId).ToListAsync(ct));
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("SCIM_GROUP_DEPROVISIONED", entityName: nameof(DirectoryGroup), entityId: id.ToString(), metadata: new { principal.ApplicationSystemId, principal.TokenId }, ct: ct);
        return OperationResult.Success();
    }

    private async Task<OperationResult<ScimResource>> WriteGroupAsync(
        ProvisioningPrincipal principal, Guid id, string? ifMatch,
        Func<IReadOnlyCollection<Guid>, GroupChanges, (string Code, string Message)?> collect, CancellationToken ct)
    {
        var appId = principal.ApplicationSystemId;
        var row = await GroupRows(appId, tracked: true).SingleOrDefaultAsync(candidate => candidate.Group.Id == id, ct);
        if (row is null)
            return GroupNotFound();
        var current = (await GroupResourcesAsync([row], ct))[0];
        if (!ScimJson.IfMatchAllows(ifMatch, current.Version))
            return Failure<ScimResource>("preconditionFailed", "The group changed since the version in If-Match.");
        var memberships = await _db.UserGroupMemberships.Where(item => item.GroupId == id).ToListAsync(ct);
        var members = memberships.Select(item => item.UserId).ToHashSet();

        var changes = new GroupChanges();
        if (collect(members, changes) is { } error)
            return Failure<ScimResource>(error.Code, error.Message);
        var group = row.Group;
        if (changes.DisplayName is { } displayName && !string.Equals(displayName, group.Name, StringComparison.Ordinal))
        {
            var normalized = NormalizeGroupName(displayName);
            if (await _db.DirectoryGroups.AnyAsync(item => item.NormalizedName == normalized && item.Id != group.Id, ct))
                return Failure<ScimResource>("uniqueness", "displayName already exists.");
            group.Name = displayName;
            group.NormalizedName = normalized;
        }

        var desired = changes.Members ?? members;
        var added = desired.Except(members).ToList();
        var removed = members.Except(desired).ToList();
        if (added.Count > 0 || removed.Count > 0)
        {
            if (await _dynamicGroups.IsRuleManagedAsync(group.Id, ct))
                return Failure<ScimResource>("mutability", "The members of this group follow its profile rules in AuthCenter.");
            if (await ForeignMemberAsync(appId, added, ct) is { } foreign)
                return Failure<ScimResource>("invalidValue", $"Member {foreign} is not a user of this application.");
        }
        var now = _clock.UtcNow;
        foreach (var userId in added)
            _db.UserGroupMemberships.Add(new UserGroupMembership { GroupId = group.Id, UserId = userId, CreatedAt = now });
        _db.UserGroupMemberships.RemoveRange(memberships.Where(item => removed.Contains(item.UserId)));
        var applications = await _db.GroupApplicationAssignments.Where(item => item.GroupId == group.Id).Select(item => item.ApplicationSystemId).ToListAsync(ct);
        await RevokeGroupSessionsAsync(group.Id, added.Concat(removed), applications, ct);

        if (changes.ExternalIdChanged && !string.Equals(row.Link?.ExternalId, changes.ExternalId, StringComparison.Ordinal))
        {
            if (row.Link is { } link)
            {
                link.ExternalId = changes.ExternalId;
                link.UpdatedAt = now;
            }
            else
            {
                _db.ScimResourceLinks.Add(new ScimResourceLink { Id = Guid.NewGuid(), ApplicationSystemId = appId, ResourceType = GroupType, ResourceId = group.Id, ExternalId = changes.ExternalId, CreatedAt = now, UpdatedAt = now });
            }
        }
        group.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("SCIM_GROUP_UPDATED", entityName: nameof(DirectoryGroup), entityId: id.ToString(), metadata: new { principal.ApplicationSystemId, principal.TokenId, membersAdded = added.Count, membersRemoved = removed.Count }, ct: ct);
        return await GetGroupAsync(principal, id, ct);
    }

    private static (string Code, string Message)? ApplyGroupObject(JsonElement value, GroupChanges changes)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!ScimPath.TryParse(property.Name, out var path))
                return ("invalidPath", $"{property.Name} is not a valid attribute path.");
            if (ApplyGroupOperation("replace", path, property.Value, changes) is { } error)
                return error;
        }
        return null;
    }

    private static (string Code, string Message)? ApplyGroupOperation(string op, ScimPath path, JsonElement value, GroupChanges changes)
    {
        if (path.Schema is not null)
            return null;
        switch (path.Attribute.ToLowerInvariant())
        {
            case "displayname" when path.Filter is null && path.SubAttribute is null:
                if (op == "remove")
                    return ("mutability", "displayName is required and cannot be removed.");
                if (!TryText(value, out var displayName) || displayName.Length > MaxNameLength)
                    return ("invalidValue", $"displayName must be text of at most {MaxNameLength} characters.");
                changes.DisplayName = displayName;
                return null;
            case "externalid" when path.Filter is null && path.SubAttribute is null:
                if (op == "remove" || value.ValueKind == JsonValueKind.Null)
                    changes.SetExternalId(null);
                else if (TryText(value, out var externalId) && externalId.Length <= MaxExternalIdLength)
                    changes.SetExternalId(externalId);
                else
                    return ("invalidValue", $"externalId must be text of at most {MaxExternalIdLength} characters.");
                return null;
            case "members":
                return ApplyMembersOperation(op, path, value, changes);
            default:
                return ("invalidPath", "Groups can change displayName, externalId and members.");
        }
    }

    /// <summary>
    /// Members by value (<c>members[value eq "id"]</c>) or as a list: add joins, replace sets, remove
    /// takes the listed members out (Entra ID) or, without a value, all of them.
    /// </summary>
    private static (string Code, string Message)? ApplyMembersOperation(string op, ScimPath path, JsonElement value, GroupChanges changes)
    {
        var members = changes.Members!;
        if (path.Filter is not null)
        {
            if (op != "remove" || !path.Filter.Attribute.Equals("value", StringComparison.OrdinalIgnoreCase) || path.SubAttribute is not null)
                return ("invalidPath", "Members are selected by value to be removed: members[value eq \"id\"].");
            if (path.Filter.Value.ValueKind == JsonValueKind.String && Guid.TryParse(path.Filter.Value.GetString(), out var selected))
                members.Remove(selected);
            return null;
        }
        if (path.SubAttribute is not null)
            return ("invalidPath", "Members are changed as a whole or selected by value.");
        if (op == "remove" && value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            members.Clear();
            return null;
        }
        if (!TryMembers(value, out var listed))
            return ("invalidValue", "Members are a list of objects whose value is a user id.");
        switch (op)
        {
            case "add": members.UnionWith(listed); break;
            case "remove": members.ExceptWith(listed); break;
            default:
                members.Clear();
                members.UnionWith(listed);
                break;
        }
        return null;
    }

    private static OperationResult<GroupInput> ReadGroup(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return OperationResult<GroupInput>.Failure("invalidSyntax", "The request body must be a SCIM Group.");
        if (!ScimPath.TryProperty(payload, "displayName", out var nameElement) || !TryText(nameElement, out var displayName) || displayName.Length > MaxNameLength)
            return OperationResult<GroupInput>.Failure("invalidValue", $"displayName is required, at most {MaxNameLength} characters.");
        string? externalId = null;
        if (ScimPath.TryProperty(payload, "externalId", out var externalIdElement) && externalIdElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryText(externalIdElement, out var text) || text.Length > MaxExternalIdLength)
                return OperationResult<GroupInput>.Failure("invalidValue", $"externalId must be text of at most {MaxExternalIdLength} characters.");
            externalId = text;
        }
        IReadOnlyList<Guid>? members = null;
        if (ScimPath.TryProperty(payload, "members", out var membersElement) && membersElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryMembers(membersElement, out var listed))
                return OperationResult<GroupInput>.Failure("invalidValue", "Members are a list of objects whose value is a user id.");
            members = listed.ToList();
        }
        return OperationResult<GroupInput>.Success(new GroupInput(displayName, externalId, members));
    }

    private static bool TryMembers(JsonElement value, out HashSet<Guid> members)
    {
        members = [];
        var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [value];
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object || !ScimPath.TryProperty(item, "value", out var id) ||
                id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var userId))
                return false;
            members.Add(userId);
        }
        return true;
    }

    /// <summary>The first listed user without access to the application, if any.</summary>
    private async Task<Guid?> ForeignMemberAsync(Guid appId, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
            return null;
        var ids = userIds.ToList();
        var known = await _db.UserApplicationAccesses.AsNoTracking()
            .Where(access => access.ApplicationSystemId == appId && ids.Contains(access.UserId))
            .Select(access => access.UserId).Distinct().ToListAsync(ct);
        var foreign = ids.Except(known).ToList();
        return foreign.Count == 0 ? null : foreign[0];
    }

    /// <summary>A membership change ends the sessions of the group's applications for the users concerned.</summary>
    private async Task RevokeGroupSessionsAsync(Guid groupId, IEnumerable<Guid> userIds, IReadOnlyCollection<Guid> applicationIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0 || applicationIds.Count == 0)
            return;
        var applications = applicationIds.ToList();
        var codes = await _db.ApplicationSystems.Where(application => applications.Contains(application.Id)).Select(application => application.Code).ToListAsync(ct);
        var now = _clock.UtcNow;
        foreach (var token in await _db.RefreshTokens.Where(token => ids.Contains(token.UserId) && codes.Contains(token.ApplicationCode) && token.RevokedAt == null).ToListAsync(ct))
            token.RevokedAt = now;
    }

    private IQueryable<GroupRow> GroupRows(Guid appId, bool tracked = false)
    {
        var assignments = tracked ? _db.GroupApplicationAssignments : _db.GroupApplicationAssignments.AsNoTracking();
        var groups = tracked ? _db.DirectoryGroups : _db.DirectoryGroups.AsNoTracking();
        var links = tracked ? _db.ScimResourceLinks : _db.ScimResourceLinks.AsNoTracking();
        return from assignment in assignments
               where assignment.ApplicationSystemId == appId
               join grp in groups on assignment.GroupId equals grp.Id
               join link in links.Where(link => link.ApplicationSystemId == appId && link.ResourceType == GroupType) on grp.Id equals link.ResourceId into groupLinks
               from link in groupLinks.DefaultIfEmpty()
               select new GroupRow { Group = grp, Link = link };
    }

    private async Task<List<ScimResource>> GroupResourcesAsync(IReadOnlyList<GroupRow> rows, CancellationToken ct)
    {
        var ids = rows.Select(row => row.Group.Id).ToList();
        var members = ids.Count == 0
            ? []
            : await _db.UserGroupMemberships.AsNoTracking()
                .Where(membership => ids.Contains(membership.GroupId))
                .Select(membership => new { membership.GroupId, membership.UserId, membership.User.FullName })
                .ToListAsync(ct);
        return rows.Select(row =>
        {
            var group = row.Group;
            var body = new JsonObject
            {
                ["schemas"] = new JsonArray(ScimPath.CoreGroupSchema),
                ["id"] = group.Id.ToString()
            };
            if (row.Link?.ExternalId is { } externalId)
                body["externalId"] = externalId;
            body["displayName"] = group.Name;
            body["members"] = new JsonArray(members
                .Where(member => member.GroupId == group.Id)
                .OrderBy(member => member.UserId)
                .Select(member => (JsonNode)new JsonObject { ["value"] = member.UserId.ToString(), ["display"] = member.FullName, ["type"] = UserType })
                .ToArray());
            var version = ScimJson.Version(body);
            body["meta"] = new JsonObject
            {
                ["resourceType"] = GroupType,
                ["created"] = ScimJson.Timestamp(group.CreatedAt),
                ["lastModified"] = ScimJson.Timestamp(group.UpdatedAt ?? group.CreatedAt),
                ["version"] = version
            };
            return new ScimResource(group.Id, body, version);
        }).ToList();
    }

    private static string NormalizeGroupName(string name) => name.Trim().ToUpperInvariant();

    private static OperationResult<ScimResource> GroupNotFound() => OperationResult<ScimResource>.Failure("notFound", "SCIM group not found.");

    private sealed class GroupRow
    {
        public DirectoryGroup Group { get; init; } = null!;
        public ScimResourceLink? Link { get; init; }
    }

    private sealed record GroupInput(string DisplayName, string? ExternalId, IReadOnlyList<Guid>? Members);

    private sealed class GroupChanges
    {
        public string? DisplayName { get; set; }
        public bool ExternalIdChanged { get; private set; }
        public string? ExternalId { get; private set; }
        public HashSet<Guid>? Members { get; set; }

        public void SetExternalId(string? value)
        {
            ExternalIdChanged = true;
            ExternalId = value;
        }
    }
}
