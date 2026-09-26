using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Lifecycle;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Services.Scim;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public sealed class LifecycleAutomationService : ILifecycleAutomationService
{
    private readonly AuthCenterDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly DynamicGroupMembershipService _dynamicGroups;

    public LifecycleAutomationService(AuthCenterDbContext db, IDateTimeProvider clock, IAuditService audit, DynamicGroupMembershipService dynamicGroups) =>
        (_db, _clock, _audit, _dynamicGroups) = (db, clock, audit, dynamicGroups);

    public async Task<PagedResult<ProfileMappingDto>> GetProfileMappingsAsync(ProfileMappingQuery query, CancellationToken ct = default)
    {
        var items = _db.ProfileMappings.AsNoTracking().Include(x => x.ApplicationSystem).Include(x => x.TargetAttributeDefinition).AsQueryable();
        if (query.ApplicationSystemId.HasValue) items = items.Where(x => x.ApplicationSystemId == query.ApplicationSystemId);
        if (query.IsActive.HasValue) items = items.Where(x => x.IsActive == query.IsActive);
        var total = await items.CountAsync(ct);
        var page = await items.OrderBy(x => x.SourcePath).Skip(query.Skip).Take(query.PageSize).ToListAsync(ct);
        return PagedResult<ProfileMappingDto>.Create(page.Select(Map).ToList(), total, query.Page, query.PageSize);
    }

    public async Task<ProfileMappingDto?> GetProfileMappingAsync(Guid id, CancellationToken ct = default)
    {
        var item = await MappingQuery().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return item is null ? null : Map(item);
    }

    public async Task<OperationResult<ProfileMappingDto>> CreateProfileMappingAsync(CreateProfileMappingRequest request, CancellationToken ct = default)
    {
        var validation = await ValidateMappingAsync(request.ApplicationSystemId, request.SourceSystem, request.SourcePath, request.TargetAttributeDefinitionId, ct);
        if (validation is not null) return OperationResult<ProfileMappingDto>.Failure(validation.Value.Code, validation.Value.Message);
        var entity = new ProfileMapping { Id = Guid.NewGuid(), ApplicationSystemId = request.ApplicationSystemId, SourceSystem = "SCIM", SourcePath = request.SourcePath.Trim(), TargetAttributeDefinitionId = request.TargetAttributeDefinitionId, IsAuthoritative = request.IsAuthoritative, IsActive = true, CreatedAt = _clock.UtcNow };
        _db.ProfileMappings.Add(entity);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return OperationResult<ProfileMappingDto>.Failure("PROFILE_MAPPING_EXISTS", "This source path is already mapped."); }
        await _audit.LogAsync("PROFILE_MAPPING_CREATED", applicationCode: await ApplicationCodeAsync(entity.ApplicationSystemId, ct), entityName: nameof(ProfileMapping), entityId: entity.Id.ToString(), metadata: new { result = "Success", entity.SourceSystem, entity.SourcePath, entity.TargetAttributeDefinitionId }, ct: ct);
        return OperationResult<ProfileMappingDto>.Success(Map(await MappingQuery().AsNoTracking().SingleAsync(x => x.Id == entity.Id, ct)));
    }

    public async Task<OperationResult> ValidateProfileMappingAsync(CreateProfileMappingRequest request, CancellationToken ct = default)
    {
        var validation = await ValidateMappingAsync(request.ApplicationSystemId, request.SourceSystem, request.SourcePath, request.TargetAttributeDefinitionId, ct);
        return validation is null ? OperationResult.Success() : OperationResult.Failure(validation.Value.Code, validation.Value.Message);
    }

    public async Task<OperationResult<ProfileMappingDto>> UpdateProfileMappingAsync(Guid id, UpdateProfileMappingRequest request, CancellationToken ct = default)
    {
        var item = await _db.ProfileMappings.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return OperationResult<ProfileMappingDto>.Failure("PROFILE_MAPPING_NOT_FOUND", "Mapping not found.");
        if (item.Version != request.Version) return OperationResult<ProfileMappingDto>.Failure("CONCURRENCY_CONFLICT", "The mapping changed after it was loaded.");
        var validation = await ValidateMappingAsync(item.ApplicationSystemId, request.SourceSystem, request.SourcePath, request.TargetAttributeDefinitionId, ct);
        if (validation is not null) return OperationResult<ProfileMappingDto>.Failure(validation.Value.Code, validation.Value.Message);
        item.SourceSystem = "SCIM"; item.SourcePath = request.SourcePath.Trim(); item.TargetAttributeDefinitionId = request.TargetAttributeDefinitionId; item.IsAuthoritative = request.IsAuthoritative; item.IsActive = request.IsActive; item.Version++;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return OperationResult<ProfileMappingDto>.Failure("PROFILE_MAPPING_EXISTS", "This source path is already mapped."); }
        await _audit.LogAsync("PROFILE_MAPPING_UPDATED", applicationCode: await ApplicationCodeAsync(item.ApplicationSystemId, ct), entityName: nameof(ProfileMapping), entityId: id.ToString(), metadata: new { result = "Success", item.Version }, ct: ct);
        return OperationResult<ProfileMappingDto>.Success(Map(await MappingQuery().AsNoTracking().SingleAsync(x => x.Id == id, ct)));
    }

    public async Task<OperationResult<ProfileMappingSimulationDto>> SimulateProfileMappingAsync(Guid id, ProfileMappingSimulationRequest request, CancellationToken ct = default)
    {
        var item = await MappingQuery().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return OperationResult<ProfileMappingSimulationDto>.Failure("PROFILE_MAPPING_NOT_FOUND", "Mapping not found.");
        if (request.SourceDocument.ValueKind != JsonValueKind.Object) return OperationResult<ProfileMappingSimulationDto>.Failure("INVALID_SOURCE_DOCUMENT", "A JSON object is required.");
        JsonElement value = default;
        var found = ScimPath.TryParse(item.SourcePath, out var path) && path.TryResolve(request.SourceDocument, out value);
        return OperationResult<ProfileMappingSimulationDto>.Success(new ProfileMappingSimulationDto { IsValid = found, SourcePath = item.SourcePath, TargetAttributeName = item.TargetAttributeDefinition.Key, Value = found ? value.Clone() : null, Errors = found ? [] : ["Source path was not found."] });
    }

    public async Task<PagedResult<DynamicGroupRuleDto>> GetDynamicGroupRulesAsync(DynamicGroupRuleQuery query, CancellationToken ct = default)
    {
        var items = RuleQuery().AsNoTracking();
        if (query.DirectoryGroupId.HasValue) items = items.Where(x => x.DirectoryGroupId == query.DirectoryGroupId);
        if (query.IsActive.HasValue) items = items.Where(x => x.IsActive == query.IsActive);
        var total = await items.CountAsync(ct);
        var page = await items.OrderBy(x => x.DirectoryGroup.Name).ThenBy(x => x.ProfileAttributeDefinition.Key).Skip(query.Skip).Take(query.PageSize).ToListAsync(ct);
        return PagedResult<DynamicGroupRuleDto>.Create(page.Select(Map).ToList(), total, query.Page, query.PageSize);
    }

    public async Task<DynamicGroupRuleDto?> GetDynamicGroupRuleAsync(Guid id, CancellationToken ct = default)
    {
        var item = await RuleQuery().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return item is null ? null : Map(item);
    }

    public async Task<OperationResult<DynamicGroupRuleDto>> CreateDynamicGroupRuleAsync(CreateDynamicGroupRuleRequest request, CancellationToken ct = default)
    {
        var validation = await ValidateRuleAsync(request.DirectoryGroupId, request.ProfileAttributeDefinitionId, request.Operator, request.ExpectedValue, ct);
        if (validation.Error is { } error) return OperationResult<DynamicGroupRuleDto>.Failure(error.Code, error.Message);
        var entity = new DynamicGroupRule { Id = Guid.NewGuid(), DirectoryGroupId = request.DirectoryGroupId, ProfileAttributeDefinitionId = request.ProfileAttributeDefinitionId, Operator = request.Operator, ExpectedValueJson = validation.ExpectedJson, IsActive = true, CreatedAt = _clock.UtcNow };
        _db.DynamicGroupRules.Add(entity);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return OperationResult<DynamicGroupRuleDto>.Failure("GROUP_RULE_EXISTS", "This group already has a rule for the profile attribute."); }
        // The group follows its rules for the whole directory at once, not user by user on the next SCIM update.
        var (added, removed) = await _dynamicGroups.SynchronizeGroupAsync(entity.DirectoryGroupId, ct);
        await _audit.LogAsync("DYNAMIC_GROUP_RULE_CREATED", entityName: nameof(DynamicGroupRule), entityId: entity.Id.ToString(), metadata: new { result = "Success", entity.DirectoryGroupId, entity.ProfileAttributeDefinitionId, entity.Operator, membersAdded = added, membersRemoved = removed }, ct: ct);
        return OperationResult<DynamicGroupRuleDto>.Success(Map(await RuleQuery().AsNoTracking().SingleAsync(x => x.Id == entity.Id, ct)));
    }

    public async Task<OperationResult<DynamicGroupRuleDto>> UpdateDynamicGroupRuleAsync(Guid id, UpdateDynamicGroupRuleRequest request, CancellationToken ct = default)
    {
        var item = await _db.DynamicGroupRules.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return OperationResult<DynamicGroupRuleDto>.Failure("GROUP_RULE_NOT_FOUND", "Rule not found.");
        if (item.Version != request.Version) return OperationResult<DynamicGroupRuleDto>.Failure("CONCURRENCY_CONFLICT", "The group rule changed after it was loaded.");
        var validation = await ValidateRuleAsync(item.DirectoryGroupId, request.ProfileAttributeDefinitionId, request.Operator, request.ExpectedValue, ct);
        if (validation.Error is { } error) return OperationResult<DynamicGroupRuleDto>.Failure(error.Code, error.Message);
        item.ProfileAttributeDefinitionId = request.ProfileAttributeDefinitionId; item.Operator = request.Operator; item.ExpectedValueJson = validation.ExpectedJson; item.IsActive = request.IsActive; item.Version++;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return OperationResult<DynamicGroupRuleDto>.Failure("CONCURRENCY_CONFLICT", "The group rule changed after it was loaded."); }
        catch (DbUpdateException) { return OperationResult<DynamicGroupRuleDto>.Failure("GROUP_RULE_EXISTS", "This group already has a rule for the profile attribute."); }
        var (added, removed) = await _dynamicGroups.SynchronizeGroupAsync(item.DirectoryGroupId, ct);
        await _audit.LogAsync("DYNAMIC_GROUP_RULE_UPDATED", entityName: nameof(DynamicGroupRule), entityId: id.ToString(), metadata: new { result = "Success", item.Version, item.Operator, membersAdded = added, membersRemoved = removed }, ct: ct);
        return OperationResult<DynamicGroupRuleDto>.Success(Map(await RuleQuery().AsNoTracking().SingleAsync(x => x.Id == id, ct)));
    }

    public async Task<OperationResult<GroupRulePreviewDto>> PreviewDynamicGroupRuleAsync(Guid id, GroupRulePreviewRequest request, CancellationToken ct = default)
    {
        var rule = await _db.DynamicGroupRules.AsNoTracking().Include(x => x.ProfileAttributeDefinition).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (rule is null) return OperationResult<GroupRulePreviewDto>.Failure("GROUP_RULE_NOT_FOUND", "Rule not found.");
        // The operators are evaluated like the membership itself (GroupRuleEvaluator), not in SQL.
        var candidates = await _db.UserProfileAttributeValues.AsNoTracking()
            .Where(x => x.AttributeDefinitionId == rule.ProfileAttributeDefinitionId && x.User.IsActive && x.User.DeletedAt == null)
            .Select(x => new { x.UserId, x.ValueJson, x.User.Email, x.User.FullName })
            .ToListAsync(ct);
        var matching = candidates.Where(x => GroupRuleEvaluator.Matches(rule, rule.ProfileAttributeDefinition.DataType, x.ValueJson)).OrderBy(x => x.Email, StringComparer.OrdinalIgnoreCase).ToList();
        var users = matching.Skip(request.Skip).Take(request.PageSize).Select(x => new GroupRulePreviewUserDto { Id = x.UserId, Email = x.Email!, FullName = x.FullName }).ToList();
        return OperationResult<GroupRulePreviewDto>.Success(new GroupRulePreviewDto { RuleId = id, Users = PagedResult<GroupRulePreviewUserDto>.Create(users, matching.Count, request.Page, request.PageSize) });
    }

    public async Task<OperationResult> DeleteProfileMappingAsync(Guid id, CancellationToken ct = default)
    {
        var item = await _db.ProfileMappings.FindAsync([id], ct); if (item is null) return OperationResult.Failure("PROFILE_MAPPING_NOT_FOUND", "Mapping not found.");
        _db.Remove(item); await _db.SaveChangesAsync(ct); await _audit.LogAsync("PROFILE_MAPPING_DELETED", applicationCode: await ApplicationCodeAsync(item.ApplicationSystemId, ct), entityName: nameof(ProfileMapping), entityId: id.ToString(), metadata: new { result = "Success" }, ct: ct); return OperationResult.Success();
    }

    public async Task<OperationResult> DeleteDynamicGroupRuleAsync(Guid id, CancellationToken ct = default)
    {
        var item = await _db.DynamicGroupRules.FindAsync([id], ct); if (item is null) return OperationResult.Failure("GROUP_RULE_NOT_FOUND", "Rule not found.");
        _db.Remove(item); await _db.SaveChangesAsync(ct); await _dynamicGroups.SynchronizeGroupAsync(item.DirectoryGroupId, ct); await _audit.LogAsync("DYNAMIC_GROUP_RULE_DELETED", entityName: nameof(DynamicGroupRule), entityId: id.ToString(), metadata: new { result = "Success" }, ct: ct); return OperationResult.Success();
    }

    private IQueryable<ProfileMapping> MappingQuery() => _db.ProfileMappings.Include(x => x.ApplicationSystem).Include(x => x.TargetAttributeDefinition);
    private IQueryable<DynamicGroupRule> RuleQuery() => _db.DynamicGroupRules.Include(x => x.DirectoryGroup).Include(x => x.ProfileAttributeDefinition);
    private async Task<(string Code, string Message)?> ValidateMappingAsync(Guid appId, string sourceSystem, string sourcePath, Guid definitionId, CancellationToken ct)
    {
        if (!string.Equals(sourceSystem, "SCIM", StringComparison.OrdinalIgnoreCase) ||
            !await _db.ApplicationSystems.AnyAsync(x => x.Id == appId && x.IsActive, ct) || !await _db.UserProfileAttributeDefinitions.AnyAsync(x => x.Id == definitionId && x.IsActive, ct))
            return ("INVALID_PROFILE_MAPPING", "Active application, SCIM source path, and active target definition are required.");
        // The path is read exactly as SCIM requests are (ScimService), so what validates here maps there.
        return ScimPath.TryParse(sourcePath, out _)
            ? null
            : ("INVALID_PROFILE_MAPPING", "The source path must be a SCIM attribute path, such as name.givenName, emails[type eq \"work\"].value or urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:department.");
    }
    private async Task<((string Code, string Message)? Error, string ExpectedJson)> ValidateRuleAsync(Guid groupId, Guid definitionId, string op, JsonElement expected, CancellationToken ct)
    {
        var definition = await _db.UserProfileAttributeDefinitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == definitionId && x.IsActive, ct);
        if (definition is null || expected.ValueKind is JsonValueKind.Undefined || !await _db.DirectoryGroups.AnyAsync(x => x.Id == groupId && x.IsActive, ct))
            return (("INVALID_GROUP_RULE", "An active group, an active profile attribute and an expected value are required."), string.Empty);
        if (!GroupRuleEvaluator.Operators.Contains(op))
            return (("INVALID_GROUP_RULE_OPERATOR", $"Unknown operator {op}. Use one of: {string.Join(", ", GroupRuleEvaluator.Operators)}."), string.Empty);
        if (!GroupRuleEvaluator.Supports(op, definition.DataType))
            return (("INVALID_GROUP_RULE_OPERATOR", $"The {op} operator does not apply to {definition.DataType} attributes."), string.Empty);
        return GroupRuleEvaluator.TryNormalizeExpected(op, definition, expected, out var json, out var message)
            ? (null, json)
            : (("INVALID_GROUP_RULE_VALUE", message), string.Empty);
    }
    private Task<string?> ApplicationCodeAsync(Guid id, CancellationToken ct) => _db.ApplicationSystems.Where(x => x.Id == id).Select(x => x.Code).SingleOrDefaultAsync(ct);
    private static ProfileMappingDto Map(ProfileMapping x) => new() { Id = x.Id, ApplicationSystemId = x.ApplicationSystemId, ApplicationName = x.ApplicationSystem.Name, SourceSystem = x.SourceSystem, SourcePath = x.SourcePath, TargetAttributeDefinitionId = x.TargetAttributeDefinitionId, TargetAttributeName = x.TargetAttributeDefinition.Key, IsAuthoritative = x.IsAuthoritative, IsActive = x.IsActive, CreatedAt = x.CreatedAt, Version = x.Version };
    private static DynamicGroupRuleDto Map(DynamicGroupRule x) => new() { Id = x.Id, DirectoryGroupId = x.DirectoryGroupId, GroupName = x.DirectoryGroup.Name, ProfileAttributeDefinitionId = x.ProfileAttributeDefinitionId, AttributeName = x.ProfileAttributeDefinition.Key, Operator = x.Operator, ExpectedValue = JsonDocument.Parse(x.ExpectedValueJson).RootElement.Clone(), IsActive = x.IsActive, CreatedAt = x.CreatedAt, Version = x.Version };
}
