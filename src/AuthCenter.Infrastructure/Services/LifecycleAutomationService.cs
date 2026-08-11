using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public sealed class LifecycleAutomationService : ILifecycleAutomationService
{
    private readonly AuthCenterDbContext _db; private readonly IDateTimeProvider _clock; private readonly IAuditService _audit;
    public LifecycleAutomationService(AuthCenterDbContext db, IDateTimeProvider clock, IAuditService audit) { _db = db; _clock = clock; _audit = audit; }
    public async Task<OperationResult> CreateProfileMappingAsync(CreateProfileMappingRequest request, CancellationToken ct = default)
    {
        if (!string.Equals(request.SourceSystem, "SCIM", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(request.SourcePath) || request.SourcePath.Length > 300 || !await _db.ApplicationSystems.AnyAsync(x => x.Id == request.ApplicationSystemId && x.IsActive, ct) || !await _db.UserProfileAttributeDefinitions.AnyAsync(x => x.Id == request.TargetAttributeDefinitionId && x.IsActive, ct)) return OperationResult.Failure("INVALID_PROFILE_MAPPING", "Active application, SCIM source path, and active target definition are required.");
        var entity = new ProfileMapping { Id = Guid.NewGuid(), ApplicationSystemId = request.ApplicationSystemId, SourceSystem = "SCIM", SourcePath = request.SourcePath.Trim(), TargetAttributeDefinitionId = request.TargetAttributeDefinitionId, IsAuthoritative = request.IsAuthoritative, IsActive = true, CreatedAt = _clock.UtcNow }; _db.ProfileMappings.Add(entity); try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { return OperationResult.Failure("PROFILE_MAPPING_EXISTS", "This source path is already mapped."); }
        await _audit.LogAsync("PROFILE_MAPPING_CREATED", entityName: nameof(ProfileMapping), entityId: entity.Id.ToString(), ct: ct); return OperationResult.Success();
    }
    public async Task<OperationResult> CreateDynamicGroupRuleAsync(CreateDynamicGroupRuleRequest request, CancellationToken ct = default)
    {
        if (request.Operator != "eq" || request.ExpectedValue.ValueKind is System.Text.Json.JsonValueKind.Undefined || !await _db.DirectoryGroups.AnyAsync(x => x.Id == request.DirectoryGroupId && x.IsActive, ct) || !await _db.UserProfileAttributeDefinitions.AnyAsync(x => x.Id == request.ProfileAttributeDefinitionId && x.IsActive, ct)) return OperationResult.Failure("INVALID_GROUP_RULE", "Active group/profile definition, eq operator, and expected value are required.");
        var entity = new DynamicGroupRule { Id = Guid.NewGuid(), DirectoryGroupId = request.DirectoryGroupId, ProfileAttributeDefinitionId = request.ProfileAttributeDefinitionId, Operator = "eq", ExpectedValueJson = request.ExpectedValue.GetRawText(), IsActive = true, CreatedAt = _clock.UtcNow }; _db.DynamicGroupRules.Add(entity); try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { return OperationResult.Failure("GROUP_RULE_EXISTS", "This group already has a rule for the profile attribute."); }
        await _audit.LogAsync("DYNAMIC_GROUP_RULE_CREATED", entityName: nameof(DynamicGroupRule), entityId: entity.Id.ToString(), ct: ct); return OperationResult.Success();
    }
    public async Task<OperationResult> DeleteProfileMappingAsync(Guid id, CancellationToken ct = default) { var item = await _db.ProfileMappings.FindAsync([id], ct); if (item is null) return OperationResult.Failure("PROFILE_MAPPING_NOT_FOUND", "Mapping not found."); _db.Remove(item); await _db.SaveChangesAsync(ct); await _audit.LogAsync("PROFILE_MAPPING_DELETED", entityName: nameof(ProfileMapping), entityId: id.ToString(), ct: ct); return OperationResult.Success(); }
    public async Task<OperationResult> DeleteDynamicGroupRuleAsync(Guid id, CancellationToken ct = default) { var item = await _db.DynamicGroupRules.FindAsync([id], ct); if (item is null) return OperationResult.Failure("GROUP_RULE_NOT_FOUND", "Rule not found."); _db.Remove(item); await _db.SaveChangesAsync(ct); await _audit.LogAsync("DYNAMIC_GROUP_RULE_DELETED", entityName: nameof(DynamicGroupRule), entityId: id.ToString(), ct: ct); return OperationResult.Success(); }
}
