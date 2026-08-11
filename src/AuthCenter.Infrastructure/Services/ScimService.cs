using System.Text.Json;
using System.Text.RegularExpressions;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Profiles;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public sealed class ScimService : IScimService
{
    private const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    private const string GroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";
    private const string ListSchema = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    private static readonly Regex FilterPattern = new("^(userName|externalId|displayName)\\s+eq\\s+\"([^\"]{1,300})\"$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private readonly AuthCenterDbContext _db; private readonly UserManager<ApplicationUser> _users; private readonly IUserAccessService _access; private readonly IUserProfileService _profiles; private readonly IDateTimeProvider _clock; private readonly IAuditService _audit;
    public ScimService(AuthCenterDbContext db, UserManager<ApplicationUser> users, IUserAccessService access, IUserProfileService profiles, IDateTimeProvider clock, IAuditService audit) { _db = db; _users = users; _access = access; _profiles = profiles; _clock = clock; _audit = audit; }

    public async Task<OperationResult<object>> ListUsersAsync(ProvisioningPrincipal principal, string? filter, int startIndex, int count, CancellationToken ct = default)
    {
        var page = NormalizePage(startIndex, count); if (page is null) return Fail("invalidValue", "startIndex must be positive and count must be between 1 and 200.");
        var query = from access in _db.UserApplicationAccesses.AsNoTracking() join user in _db.Users.IgnoreQueryFilters().AsNoTracking() on access.UserId equals user.Id where access.ApplicationSystemId == principal.ApplicationSystemId select user;
        var parsed = ParseFilter(filter, "userName", "externalId"); if (!parsed.IsValid) return Fail("invalidFilter", "Only userName or externalId eq filters are supported.");
        if (parsed.Attribute == "userName") { var normalized = parsed.Value!.ToUpperInvariant(); query = query.Where(user => user.NormalizedEmail == normalized); }
        if (parsed.Attribute == "externalId") { var ids = _db.ScimResourceLinks.Where(link => link.ApplicationSystemId == principal.ApplicationSystemId && link.ResourceType == "User" && link.ExternalId == parsed.Value).Select(link => link.ResourceId); query = query.Where(user => ids.Contains(user.Id)); }
        var total = await query.CountAsync(ct); var users = await query.OrderBy(user => user.Email).Skip(page.Value.Skip).Take(page.Value.Count).ToListAsync(ct);
        var resources = new List<object>(); foreach (var user in users) resources.Add(await MapUserAsync(principal.ApplicationSystemId, user, ct));
        return Success(new { schemas = new[] { ListSchema }, totalResults = total, startIndex = page.Value.Start, itemsPerPage = resources.Count, Resources = resources });
    }

    public async Task<OperationResult<object>> GetUserAsync(ProvisioningPrincipal principal, Guid id, CancellationToken ct = default)
    {
        var user = await FindUserAsync(principal.ApplicationSystemId, id, ct); return user is null ? Fail("notFound", "SCIM user not found.") : Success(await MapUserAsync(principal.ApplicationSystemId, user, ct));
    }

    public async Task<OperationResult<object>> CreateUserAsync(ProvisioningPrincipal principal, JsonElement payload, CancellationToken ct = default)
    {
        if (!TryString(payload, "userName", out var email) || string.IsNullOrWhiteSpace(email) || email.Length > 256) return Fail("invalidValue", "userName must be a valid bounded email address.");
        if (await _users.FindByEmailAsync(email) is not null) return Fail("uniqueness", "userName already exists.");
        var displayName = TryString(payload, "displayName", out var display) ? display : email;
        var active = !payload.TryGetProperty("active", out var activeElement) || activeElement.ValueKind != JsonValueKind.False;
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = email.Trim(), UserName = email.Trim(), FullName = display!.Trim(), EmailConfirmed = true, IsExternalUser = true, HasLocalPassword = false, IsActive = active, CreatedAt = _clock.UtcNow };
        var created = await _users.CreateAsync(user); if (!created.Succeeded) return Fail("invalidValue", string.Join("; ", created.Errors.Select(item => item.Description)));
        await _access.GrantAccessAsync(user.Id, principal.ApplicationSystemId, active, ct);
        _db.ScimResourceLinks.Add(new ScimResourceLink { Id = Guid.NewGuid(), ApplicationSystemId = principal.ApplicationSystemId, ResourceType = "User", ResourceId = user.Id, ExternalId = OptionalString(payload, "externalId"), CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow });
        await ApplyMappingsAsync(principal.ApplicationSystemId, user.Id, payload, ct); await EvaluateGroupRulesAsync(user.Id, ct); await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("SCIM_USER_CREATED", user.Id, entityName: nameof(ApplicationUser), entityId: user.Id.ToString(), metadata: new { principal.ApplicationSystemId, principal.TokenId }, ct: ct);
        return Success(await MapUserAsync(principal.ApplicationSystemId, user, ct));
    }

    public async Task<OperationResult<object>> PatchUserAsync(ProvisioningPrincipal principal, Guid id, JsonElement payload, CancellationToken ct = default)
    {
        var user = await FindUserAsync(principal.ApplicationSystemId, id, ct); if (user is null) return Fail("notFound", "SCIM user not found.");
        if (!TryProperty(payload, "Operations", out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() is 0 or > 100) return Fail("invalidSyntax", "PATCH requires 1-100 Operations.");
        var mapped = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in operations.EnumerateArray())
        {
            var op = OptionalString(operation, "op")?.ToLowerInvariant(); var path = OptionalString(operation, "path"); operation.TryGetProperty("value", out var value);
            if (op is not ("add" or "replace" or "remove") || string.IsNullOrWhiteSpace(path)) return Fail("invalidSyntax", "Each operation requires add, replace, or remove and a path.");
            if (path.Equals("active", StringComparison.OrdinalIgnoreCase)) user.IsActive = op != "remove" && value.ValueKind == JsonValueKind.True;
            else if (path.Equals("userName", StringComparison.OrdinalIgnoreCase) && op != "remove" && value.ValueKind == JsonValueKind.String) { var email = value.GetString()!; var other = await _users.FindByEmailAsync(email); if (other is not null && other.Id != user.Id) return Fail("uniqueness", "userName already exists."); user.Email = email; user.UserName = email; user.NormalizedEmail = _users.NormalizeEmail(email); user.NormalizedUserName = _users.NormalizeName(email); }
            else if ((path.Equals("displayName", StringComparison.OrdinalIgnoreCase) || path.Equals("name.formatted", StringComparison.OrdinalIgnoreCase)) && op != "remove" && value.ValueKind == JsonValueKind.String) user.FullName = value.GetString()!;
            else if (path.Equals("externalId", StringComparison.OrdinalIgnoreCase)) { var link = await _db.ScimResourceLinks.SingleAsync(item => item.ApplicationSystemId == principal.ApplicationSystemId && item.ResourceType == "User" && item.ResourceId == user.Id, ct); link.ExternalId = op == "remove" ? null : value.GetString(); link.UpdatedAt = _clock.UtcNow; }
            else mapped[path] = op == "remove" ? JsonDocument.Parse("null").RootElement.Clone() : value.Clone();
        }
        user.UpdatedAt = _clock.UtcNow; await _users.UpdateAsync(user); await SetAccessStateAsync(user.Id, principal.ApplicationSystemId, user.IsActive, ct);
        if (mapped.Count > 0) await ApplyMappedValuesAsync(principal.ApplicationSystemId, user.Id, mapped, ct); await EvaluateGroupRulesAsync(user.Id, ct); await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(user.IsActive ? "SCIM_USER_UPDATED" : "SCIM_USER_DEPROVISIONED", user.Id, entityName: nameof(ApplicationUser), entityId: user.Id.ToString(), metadata: new { principal.ApplicationSystemId }, ct: ct);
        return Success(await MapUserAsync(principal.ApplicationSystemId, user, ct));
    }

    public async Task<OperationResult> DeleteUserAsync(ProvisioningPrincipal principal, Guid id, CancellationToken ct = default)
    {
        var user = await FindUserAsync(principal.ApplicationSystemId, id, ct); if (user is null) return OperationResult.Failure("notFound", "SCIM user not found."); user.IsActive = false; user.UpdatedAt = _clock.UtcNow; await _users.UpdateAsync(user); await SetAccessStateAsync(id, principal.ApplicationSystemId, false, ct); await _audit.LogAsync("SCIM_USER_DEPROVISIONED", id, entityName: nameof(ApplicationUser), entityId: id.ToString(), metadata: new { principal.ApplicationSystemId }, ct: ct); return OperationResult.Success();
    }

    public async Task<OperationResult<object>> ListGroupsAsync(ProvisioningPrincipal principal, string? filter, int startIndex, int count, CancellationToken ct = default)
    {
        var page = NormalizePage(startIndex, count); if (page is null) return Fail("invalidValue", "Invalid pagination."); var parsed = ParseFilter(filter, "displayName", "externalId"); if (!parsed.IsValid) return Fail("invalidFilter", "Only displayName or externalId eq filters are supported.");
        var query = from assignment in _db.GroupApplicationAssignments.AsNoTracking() join grp in _db.DirectoryGroups.AsNoTracking() on assignment.GroupId equals grp.Id where assignment.ApplicationSystemId == principal.ApplicationSystemId select grp;
        if (parsed.Attribute == "displayName") { var normalized = parsed.Value!.ToUpperInvariant(); query = query.Where(group => group.NormalizedName == normalized); }
        if (parsed.Attribute == "externalId") { var ids = _db.ScimResourceLinks.Where(link => link.ApplicationSystemId == principal.ApplicationSystemId && link.ResourceType == "Group" && link.ExternalId == parsed.Value).Select(link => link.ResourceId); query = query.Where(group => ids.Contains(group.Id)); }
        var total = await query.CountAsync(ct); var groups = await query.OrderBy(group => group.Name).Skip(page.Value.Skip).Take(page.Value.Count).ToListAsync(ct); var resources = new List<object>(); foreach (var group in groups) resources.Add(await MapGroupAsync(principal.ApplicationSystemId, group, ct)); return Success(new { schemas = new[] { ListSchema }, totalResults = total, startIndex = page.Value.Start, itemsPerPage = resources.Count, Resources = resources });
    }

    public async Task<OperationResult<object>> GetGroupAsync(ProvisioningPrincipal principal, Guid id, CancellationToken ct = default) { var group = await FindGroupAsync(principal.ApplicationSystemId, id, ct); return group is null ? Fail("notFound", "SCIM group not found.") : Success(await MapGroupAsync(principal.ApplicationSystemId, group, ct)); }

    public async Task<OperationResult<object>> CreateGroupAsync(ProvisioningPrincipal principal, JsonElement payload, CancellationToken ct = default)
    {
        if (!TryString(payload, "displayName", out var name) || string.IsNullOrWhiteSpace(name) || name.Length > 200) return Fail("invalidValue", "displayName is required."); var normalized = name.Trim().ToUpperInvariant(); if (await _db.DirectoryGroups.AnyAsync(item => item.NormalizedName == normalized, ct)) return Fail("uniqueness", "displayName already exists.");
        var group = new DirectoryGroup { Id = Guid.NewGuid(), Name = name.Trim(), NormalizedName = normalized, IsActive = true, CreatedAt = _clock.UtcNow }; _db.DirectoryGroups.Add(group); _db.GroupApplicationAssignments.Add(new GroupApplicationAssignment { GroupId = group.Id, ApplicationSystemId = principal.ApplicationSystemId, CreatedAt = _clock.UtcNow }); _db.ScimResourceLinks.Add(new ScimResourceLink { Id = Guid.NewGuid(), ApplicationSystemId = principal.ApplicationSystemId, ResourceType = "Group", ResourceId = group.Id, ExternalId = OptionalString(payload, "externalId"), CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow });
        var addedMembers = await AddMembersAsync(principal.ApplicationSystemId, group.Id, payload, ct); if (addedMembers is null) return Fail("invalidValue", "Every group member must be a user assigned to this application."); await RevokeGroupSessionsAsync(group.Id, addedMembers, ct); await _db.SaveChangesAsync(ct); await _audit.LogAsync("SCIM_GROUP_CREATED", entityName: nameof(DirectoryGroup), entityId: group.Id.ToString(), metadata: new { principal.ApplicationSystemId }, ct: ct); return Success(await MapGroupAsync(principal.ApplicationSystemId, group, ct));
    }

    public async Task<OperationResult<object>> PatchGroupAsync(ProvisioningPrincipal principal, Guid id, JsonElement payload, CancellationToken ct = default)
    {
        var group = await FindGroupAsync(principal.ApplicationSystemId, id, ct); if (group is null) return Fail("notFound", "SCIM group not found."); if (!TryProperty(payload, "Operations", out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() is 0 or > 100) return Fail("invalidSyntax", "PATCH requires 1-100 Operations.");
        var changedMembers = new HashSet<Guid>(); foreach (var operation in operations.EnumerateArray()) { var op = OptionalString(operation, "op")?.ToLowerInvariant(); var path = OptionalString(operation, "path"); operation.TryGetProperty("value", out var value); if (op is not ("add" or "replace" or "remove") || string.IsNullOrWhiteSpace(path)) return Fail("invalidSyntax", "Invalid group operation."); if (path.Equals("displayName", StringComparison.OrdinalIgnoreCase) && op != "remove" && value.ValueKind == JsonValueKind.String) { group.Name = value.GetString()!; group.NormalizedName = group.Name.ToUpperInvariant(); } else if (path.StartsWith("members", StringComparison.OrdinalIgnoreCase)) { var changed = await ApplyMemberOperationAsync(principal.ApplicationSystemId, group.Id, op, path, value, ct); if (changed is null) return Fail("invalidValue", "Every group member must be a user assigned to this application."); changedMembers.UnionWith(changed); } else return Fail("invalidPath", "Only displayName and members can be patched."); }
        await RevokeGroupSessionsAsync(group.Id, changedMembers, ct);
        group.UpdatedAt = _clock.UtcNow; await _db.SaveChangesAsync(ct); await _audit.LogAsync("SCIM_GROUP_UPDATED", entityName: nameof(DirectoryGroup), entityId: id.ToString(), metadata: new { principal.ApplicationSystemId }, ct: ct); return Success(await MapGroupAsync(principal.ApplicationSystemId, group, ct));
    }

    public async Task<OperationResult> DeleteGroupAsync(ProvisioningPrincipal principal, Guid id, CancellationToken ct = default) { var group = await FindGroupAsync(principal.ApplicationSystemId, id, ct); if (group is null) return OperationResult.Failure("notFound", "SCIM group not found."); group.IsActive = false; group.UpdatedAt = _clock.UtcNow; var assignments = await _db.GroupApplicationAssignments.Where(item => item.GroupId == id && item.ApplicationSystemId == principal.ApplicationSystemId).ToListAsync(ct); _db.GroupApplicationAssignments.RemoveRange(assignments); await _db.SaveChangesAsync(ct); await _audit.LogAsync("SCIM_GROUP_DEPROVISIONED", entityName: nameof(DirectoryGroup), entityId: id.ToString(), metadata: new { principal.ApplicationSystemId }, ct: ct); return OperationResult.Success(); }

    private async Task ApplyMappingsAsync(Guid appId, Guid userId, JsonElement payload, CancellationToken ct) { var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase); foreach (var mapping in await _db.ProfileMappings.AsNoTracking().Where(item => item.ApplicationSystemId == appId && item.SourceSystem == "SCIM" && item.IsActive).Include(item => item.TargetAttributeDefinition).ToListAsync(ct)) if (TryPath(payload, mapping.SourcePath, out var value)) values[mapping.SourcePath] = value; await ApplyMappedValuesAsync(appId, userId, values, ct); }
    private async Task ApplyMappedValuesAsync(Guid appId, Guid userId, IReadOnlyDictionary<string, JsonElement> sourceValues, CancellationToken ct) { var mappings = await _db.ProfileMappings.AsNoTracking().Where(item => item.ApplicationSystemId == appId && item.SourceSystem == "SCIM" && item.IsActive).Include(item => item.TargetAttributeDefinition).ToListAsync(ct); var attributes = new Dictionary<string, JsonElement?>(StringComparer.OrdinalIgnoreCase); foreach (var mapping in mappings) if (sourceValues.TryGetValue(mapping.SourcePath, out var value)) attributes[mapping.TargetAttributeDefinition.Key] = value; if (attributes.Count > 0) { var result = await _profiles.UpdateUserProfileFromSourceAsync(userId, new UpdateUserProfileRequest { Attributes = attributes }, "SCIM", ct); if (!result.IsSuccess) throw new InvalidOperationException($"Invalid SCIM profile mapping: {result.ErrorCode}"); } }
    private async Task EvaluateGroupRulesAsync(Guid userId, CancellationToken ct) { var values = await _db.UserProfileAttributeValues.AsNoTracking().Where(item => item.UserId == userId).ToDictionaryAsync(item => item.AttributeDefinitionId, item => item.ValueJson, ct); var rules = await _db.DynamicGroupRules.Where(item => item.IsActive).ToListAsync(ct); foreach (var rule in rules) { var match = values.TryGetValue(rule.ProfileAttributeDefinitionId, out var actual) && rule.Operator == "eq" && actual == rule.ExpectedValueJson; var membership = await _db.UserGroupMemberships.FindAsync([rule.DirectoryGroupId, userId], ct); var changed = false; if (match && membership is null) { _db.UserGroupMemberships.Add(new UserGroupMembership { GroupId = rule.DirectoryGroupId, UserId = userId, CreatedAt = _clock.UtcNow }); changed = true; } if (!match && membership is not null) { _db.UserGroupMemberships.Remove(membership); changed = true; } if (changed) await RevokeGroupSessionsAsync(rule.DirectoryGroupId, [userId], ct); } }
    private async Task SetAccessStateAsync(Guid userId, Guid appId, bool active, CancellationToken ct) { var access = await _db.UserApplicationAccesses.SingleAsync(item => item.UserId == userId && item.ApplicationSystemId == appId, ct); access.IsActive = active; await _db.SaveChangesAsync(ct); }
    private async Task<IReadOnlyList<Guid>?> AddMembersAsync(Guid appId, Guid groupId, JsonElement payload, CancellationToken ct) { var added = new List<Guid>(); if (!payload.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array) return added; foreach (var member in members.EnumerateArray()) { if (!Guid.TryParse(OptionalString(member, "value"), out var userId) || !await _db.UserApplicationAccesses.AnyAsync(x => x.ApplicationSystemId == appId && x.UserId == userId, ct)) return null; _db.UserGroupMemberships.Add(new UserGroupMembership { GroupId = groupId, UserId = userId, CreatedAt = _clock.UtcNow }); added.Add(userId); } return added; }
    private async Task<IReadOnlyList<Guid>?> ApplyMemberOperationAsync(Guid appId, Guid groupId, string op, string path, JsonElement value, CancellationToken ct) { var changed = new List<Guid>(); if (op == "remove") { var idText = Regex.Match(path, "value\\s+eq\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value; if (Guid.TryParse(idText, out var id)) { var entity = await _db.UserGroupMemberships.FindAsync([groupId, id], ct); if (entity is not null) { _db.UserGroupMemberships.Remove(entity); changed.Add(id); } } return changed; } var members = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : new[] { value }.AsEnumerable(); foreach (var member in members) { if (!Guid.TryParse(OptionalString(member, "value"), out var userId) || !await _db.UserApplicationAccesses.AnyAsync(x => x.ApplicationSystemId == appId && x.UserId == userId, ct)) return null; if (!await _db.UserGroupMemberships.AnyAsync(x => x.GroupId == groupId && x.UserId == userId, ct)) { _db.UserGroupMemberships.Add(new UserGroupMembership { GroupId = groupId, UserId = userId, CreatedAt = _clock.UtcNow }); changed.Add(userId); } } return changed; }
    private async Task RevokeGroupSessionsAsync(Guid groupId, IEnumerable<Guid> userIds, CancellationToken ct) { var ids = userIds.Distinct().ToArray(); if (ids.Length == 0) return; var appCodes = await (from assignment in _db.GroupApplicationAssignments where assignment.GroupId == groupId join app in _db.ApplicationSystems on assignment.ApplicationSystemId equals app.Id select app.Code).ToListAsync(ct); var tokens = await _db.RefreshTokens.Where(x => ids.Contains(x.UserId) && appCodes.Contains(x.ApplicationCode) && x.RevokedAt == null).ToListAsync(ct); foreach (var token in tokens) token.RevokedAt = _clock.UtcNow; }
    private async Task<ApplicationUser?> FindUserAsync(Guid appId, Guid id, CancellationToken ct) => await (from access in _db.UserApplicationAccesses join user in _db.Users.IgnoreQueryFilters() on access.UserId equals user.Id where access.ApplicationSystemId == appId && user.Id == id select user).SingleOrDefaultAsync(ct);
    private async Task<DirectoryGroup?> FindGroupAsync(Guid appId, Guid id, CancellationToken ct) => await (from assignment in _db.GroupApplicationAssignments join grp in _db.DirectoryGroups on assignment.GroupId equals grp.Id where assignment.ApplicationSystemId == appId && grp.Id == id select grp).SingleOrDefaultAsync(ct);
    private async Task<object> MapUserAsync(Guid appId, ApplicationUser user, CancellationToken ct) { var link = await _db.ScimResourceLinks.AsNoTracking().SingleOrDefaultAsync(item => item.ApplicationSystemId == appId && item.ResourceType == "User" && item.ResourceId == user.Id, ct); return new { schemas = new[] { UserSchema }, id = user.Id.ToString(), externalId = link?.ExternalId, userName = user.Email, displayName = user.FullName, name = new { formatted = user.FullName }, active = user.IsActive, meta = new { resourceType = "User", created = link?.CreatedAt ?? user.CreatedAt, lastModified = link?.UpdatedAt ?? user.UpdatedAt ?? user.CreatedAt } }; }
    private async Task<object> MapGroupAsync(Guid appId, DirectoryGroup group, CancellationToken ct) { var link = await _db.ScimResourceLinks.AsNoTracking().SingleOrDefaultAsync(item => item.ApplicationSystemId == appId && item.ResourceType == "Group" && item.ResourceId == group.Id, ct); var members = await _db.UserGroupMemberships.AsNoTracking().Where(item => item.GroupId == group.Id).Select(item => new { value = item.UserId.ToString() }).ToListAsync(ct); return new { schemas = new[] { GroupSchema }, id = group.Id.ToString(), externalId = link?.ExternalId, displayName = group.Name, members, meta = new { resourceType = "Group", created = link?.CreatedAt ?? group.CreatedAt, lastModified = link?.UpdatedAt ?? group.UpdatedAt ?? group.CreatedAt } }; }
    private static (string? Attribute, string? Value, bool IsValid) ParseFilter(string? filter, params string[] allowed) { if (string.IsNullOrWhiteSpace(filter)) return (null, null, true); var match = FilterPattern.Match(filter.Trim()); return match.Success && allowed.Contains(match.Groups[1].Value, StringComparer.OrdinalIgnoreCase) ? (match.Groups[1].Value, match.Groups[2].Value, true) : (null, null, false); }
    private static (int Start, int Skip, int Count)? NormalizePage(int start, int count) => start < 1 || count is < 1 or > 200 ? null : (start, start - 1, count);
    private static bool TryString(JsonElement element, string name, out string? value) { value = null; if (!element.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String) return false; value = item.GetString(); return true; }
    private static string? OptionalString(JsonElement element, string name) => TryString(element, name, out var value) ? value : null;
    private static bool TryPath(JsonElement element, string path, out JsonElement value) { value = element; foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries)) if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return false; return true; }
    private static bool TryProperty(JsonElement element, string name, out JsonElement value) { if (element.TryGetProperty(name, out value)) return true; foreach (var property in element.EnumerateObject()) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; } value = default; return false; }
    private static OperationResult<object> Success(object data) => OperationResult<object>.Success(data);
    private static OperationResult<object> Fail(string code, string message) => OperationResult<object>.Failure(code, message);
}
