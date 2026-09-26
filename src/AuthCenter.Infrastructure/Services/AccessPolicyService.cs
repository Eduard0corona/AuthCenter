using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Policies;
using AuthCenter.Contracts.Responses.Policies;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public sealed class AccessPolicyService : IAccessPolicyService
{
    private const int MaxCidrsPerCondition = 50;
    private readonly AuthCenterDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUserService _currentUser;

    public AccessPolicyService(AuthCenterDbContext db, IDateTimeProvider clock, ICurrentUserService currentUser)
    {
        _db = db;
        _clock = clock;
        _currentUser = currentUser;
    }

    public Task<IReadOnlyList<AccessPolicyRuleDto>> GetByApplicationAsync(
        Guid applicationSystemId,
        CancellationToken ct = default) =>
        GetByApplicationAsync(applicationSystemId, null, ct);

    public async Task<IReadOnlyList<AccessPolicyRuleDto>> GetByApplicationAsync(
        Guid applicationSystemId,
        Guid? policyVersionId,
        CancellationToken ct = default)
    {
        var version = await SelectManagementVersionAsync(applicationSystemId, policyVersionId, ct);
        if (version is null)
            return [];

        var rules = await BaseRuleQuery()
            .Where(rule => rule.PolicyVersionId == version.Id)
            .OrderBy(rule => rule.Priority)
            .ToListAsync(ct);
        return rules.Select(MapRule).ToList();
    }

    public async Task<IReadOnlyList<AccessPolicyVersionDto>> GetVersionsAsync(
        Guid applicationSystemId,
        CancellationToken ct = default)
    {
        var versions = await _db.ApplicationAccessPolicyVersions
            .AsNoTracking()
            .Where(version => version.ApplicationSystemId == applicationSystemId)
            .OrderByDescending(version => version.VersionNumber)
            .Select(version => new AccessPolicyVersionDto
            {
                Id = version.Id,
                ApplicationSystemId = version.ApplicationSystemId,
                VersionNumber = version.VersionNumber,
                Status = version.Status.ToString(),
                RuleCount = version.Rules.Count,
                CreatedAt = version.CreatedAt,
                PublishedAt = version.PublishedAt
            })
            .ToListAsync(ct);
        return versions;
    }

    public async Task<OperationResult<AccessPolicyVersionDto>> CreateDraftAsync(
        Guid applicationSystemId,
        CancellationToken ct = default)
    {
        var application = await _db.ApplicationSystems.FindAsync([applicationSystemId], ct);
        if (application is null)
            return VersionFailure("APP_NOT_FOUND", "Application not found.");

        var existingDraft = await _db.ApplicationAccessPolicyVersions
            .Include(version => version.Rules)
            .AsNoTracking()
            .FirstOrDefaultAsync(version =>
                version.ApplicationSystemId == applicationSystemId &&
                version.Status == AccessPolicyVersionStatus.Draft, ct);
        if (existingDraft is not null)
            return OperationResult<AccessPolicyVersionDto>.Success(MapVersion(existingDraft));

        var latestNumber = await _db.ApplicationAccessPolicyVersions
            .Where(version => version.ApplicationSystemId == applicationSystemId)
            .MaxAsync(version => (int?)version.VersionNumber, ct) ?? 0;
        var published = await _db.ApplicationAccessPolicyVersions
            .AsNoTracking()
            .Include(version => version.Rules)
            .Where(version =>
                version.ApplicationSystemId == applicationSystemId &&
                version.Status == AccessPolicyVersionStatus.Published)
            .OrderByDescending(version => version.VersionNumber)
            .FirstOrDefaultAsync(ct);

        var now = _clock.UtcNow;
        var draft = new ApplicationAccessPolicyVersion
        {
            Id = Guid.NewGuid(),
            ApplicationSystemId = applicationSystemId,
            VersionNumber = latestNumber + 1,
            Status = AccessPolicyVersionStatus.Draft,
            CreatedByUserId = _currentUser.UserId,
            CreatedAt = now,
            ApplicationSystem = application
        };
        foreach (var source in published?.Rules ?? [])
            draft.Rules.Add(CloneRule(source, draft));

        _db.ApplicationAccessPolicyVersions.Add(draft);
        AddVersionAudit("ACCESS_POLICY_DRAFT_CREATED", draft, application.Code);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return VersionFailure("POLICY_DRAFT_CONFLICT", "A draft was created concurrently. Reload the policy versions and continue with that draft.");
        }

        return OperationResult<AccessPolicyVersionDto>.Success(MapVersion(draft));
    }

    public async Task<OperationResult<AccessPolicyVersionDto>> PublishAsync(
        Guid applicationSystemId,
        Guid policyVersionId,
        CancellationToken ct = default)
    {
        var draft = await _db.ApplicationAccessPolicyVersions
            .Include(version => version.ApplicationSystem)
            .Include(version => version.Rules)
            .FirstOrDefaultAsync(version =>
                version.Id == policyVersionId &&
                version.ApplicationSystemId == applicationSystemId, ct);
        if (draft is null)
            return VersionFailure("POLICY_VERSION_NOT_FOUND", "Access policy version not found.");
        if (draft.Status != AccessPolicyVersionStatus.Draft)
            return VersionFailure("POLICY_VERSION_IMMUTABLE", "Only a draft policy version can be published.");

        if (draft.ApplicationSystem.Code == DomainConstants.SystemCodes.AuthCenter &&
            draft.Rules.Any(rule => rule.IsActive) &&
            !draft.Rules.Any(IsUnconditionalAllow))
            return VersionFailure(
                "AUTHCENTER_POLICY_FALLBACK_REQUIRED",
                "AuthCenter must retain an active unconditional Allow fallback to prevent administrative lockout.");

        var publishedVersions = await _db.ApplicationAccessPolicyVersions
            .Where(version =>
                version.ApplicationSystemId == applicationSystemId &&
                version.Status == AccessPolicyVersionStatus.Published)
            .ToListAsync(ct);
        foreach (var published in publishedVersions)
            published.Status = AccessPolicyVersionStatus.Archived;

        draft.Status = AccessPolicyVersionStatus.Published;
        draft.PublishedAt = _clock.UtcNow;
        draft.PublishedByUserId = _currentUser.UserId;
        AddVersionAudit("ACCESS_POLICY_VERSION_PUBLISHED", draft, draft.ApplicationSystem.Code);
        await SaveAndRevokeApplicationSessionsAsync(draft.ApplicationSystem.Code, ct);
        return OperationResult<AccessPolicyVersionDto>.Success(MapVersion(draft));
    }

    public async Task<OperationResult<AccessPolicyRuleDto>> CreateAsync(
        CreateAccessPolicyRuleRequest request,
        CancellationToken ct = default)
    {
        var validation = ValidateRule(
            request.Name, request.Priority, request.Action, request.MfaRequirement,
            request.IncludedIpCidrs, request.ExcludedIpCidrs, request.ActiveFromUtc,
            request.ActiveUntilUtc, request.ActiveDaysUtc, request.DailyStartTimeUtc,
            request.DailyEndTimeUtc, request.MinimumRiskLevel, request.MaximumRiskLevel,
            request.RequiredAssuranceLevel);
        if (!validation.IsSuccess)
            return RuleFailure(validation.ErrorCode, validation.Message);

        var draft = await _db.ApplicationAccessPolicyVersions
            .Include(version => version.ApplicationSystem)
            .FirstOrDefaultAsync(version =>
                version.Id == request.PolicyVersionId &&
                version.ApplicationSystemId == request.ApplicationSystemId, ct);
        if (draft is null)
            return RuleFailure("POLICY_VERSION_NOT_FOUND", "Access policy version not found for this application.");
        if (draft.Status != AccessPolicyVersionStatus.Draft)
            return RuleFailure("POLICY_VERSION_IMMUTABLE", "Published and archived policy versions are immutable; create a draft first.");

        var targetValidation = await ValidateTargetsAsync(request.UserId, request.DirectoryGroupId, ct);
        if (!targetValidation.IsSuccess)
            return RuleFailure(targetValidation.ErrorCode, targetValidation.Message);
        if (await PriorityExistsAsync(draft.Id, request.Priority, null, ct))
            return RuleFailure("POLICY_PRIORITY_TAKEN", "Another rule already uses this priority in the policy version.");

        var rule = new ApplicationAccessPolicyRule
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = draft.Id,
            ApplicationSystemId = draft.ApplicationSystemId,
            UserId = request.UserId,
            DirectoryGroupId = request.DirectoryGroupId,
            Name = request.Name.Trim(),
            Priority = request.Priority,
            Action = ParseAction(request.Action),
            MfaRequirement = ParseMfa(request.MfaRequirement),
            AllowTrustedDeviceBypass = request.AllowTrustedDeviceBypass,
            IncludedIpCidrsJson = SerializeStrings(request.IncludedIpCidrs),
            ExcludedIpCidrsJson = SerializeStrings(request.ExcludedIpCidrs),
            ActiveFromUtc = NormalizeUtc(request.ActiveFromUtc),
            ActiveUntilUtc = NormalizeUtc(request.ActiveUntilUtc),
            ActiveDaysUtcJson = SerializeDays(request.ActiveDaysUtc),
            DailyStartTimeUtc = request.DailyStartTimeUtc,
            DailyEndTimeUtc = request.DailyEndTimeUtc,
            MinimumRiskLevel = ParseOptionalRisk(request.MinimumRiskLevel),
            MaximumRiskLevel = ParseOptionalRisk(request.MaximumRiskLevel),
            RequiredAssuranceLevel = ParseAssurance(request.RequiredAssuranceLevel),
            IsActive = request.IsActive,
            CreatedAt = _clock.UtcNow,
            PolicyVersion = draft,
            ApplicationSystem = draft.ApplicationSystem
        };
        _db.ApplicationAccessPolicyRules.Add(rule);
        AddRuleAudit("ACCESS_POLICY_DRAFT_RULE_CREATED", rule, draft.ApplicationSystem.Code);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return RuleFailure("POLICY_PRIORITY_TAKEN", "Another rule already uses this priority in the policy version.");
        }

        await PopulateRuleTargetsAsync(rule, ct);
        return OperationResult<AccessPolicyRuleDto>.Success(MapRule(rule));
    }

    public async Task<OperationResult<AccessPolicyRuleDto>> UpdateAsync(
        Guid ruleId,
        UpdateAccessPolicyRuleRequest request,
        CancellationToken ct = default)
    {
        var validation = ValidateRule(
            request.Name, request.Priority, request.Action, request.MfaRequirement,
            request.IncludedIpCidrs, request.ExcludedIpCidrs, request.ActiveFromUtc,
            request.ActiveUntilUtc, request.ActiveDaysUtc, request.DailyStartTimeUtc,
            request.DailyEndTimeUtc, request.MinimumRiskLevel, request.MaximumRiskLevel,
            request.RequiredAssuranceLevel);
        if (!validation.IsSuccess)
            return RuleFailure(validation.ErrorCode, validation.Message);

        var rule = await BaseRuleQuery(tracking: true).FirstOrDefaultAsync(candidate => candidate.Id == ruleId, ct);
        if (rule is null)
            return RuleFailure("POLICY_RULE_NOT_FOUND", "Access policy rule not found.");
        if (rule.PolicyVersion.Status != AccessPolicyVersionStatus.Draft)
            return RuleFailure("POLICY_VERSION_IMMUTABLE", "Published and archived policy versions are immutable; create a draft first.");

        var targetValidation = await ValidateTargetsAsync(request.UserId, request.DirectoryGroupId, ct);
        if (!targetValidation.IsSuccess)
            return RuleFailure(targetValidation.ErrorCode, targetValidation.Message);
        if (await PriorityExistsAsync(rule.PolicyVersionId, request.Priority, rule.Id, ct))
            return RuleFailure("POLICY_PRIORITY_TAKEN", "Another rule already uses this priority in the policy version.");
        if (!rule.TryAdvance(request.Version))
            return RuleFailure(VersionedUpdates.ConflictCode, "The rule changed after it was loaded.");

        rule.UserId = request.UserId;
        rule.DirectoryGroupId = request.DirectoryGroupId;
        rule.Name = request.Name.Trim();
        rule.Priority = request.Priority;
        rule.Action = ParseAction(request.Action);
        rule.MfaRequirement = ParseMfa(request.MfaRequirement);
        rule.AllowTrustedDeviceBypass = request.AllowTrustedDeviceBypass;
        rule.IncludedIpCidrsJson = SerializeStrings(request.IncludedIpCidrs);
        rule.ExcludedIpCidrsJson = SerializeStrings(request.ExcludedIpCidrs);
        rule.ActiveFromUtc = NormalizeUtc(request.ActiveFromUtc);
        rule.ActiveUntilUtc = NormalizeUtc(request.ActiveUntilUtc);
        rule.ActiveDaysUtcJson = SerializeDays(request.ActiveDaysUtc);
        rule.DailyStartTimeUtc = request.DailyStartTimeUtc;
        rule.DailyEndTimeUtc = request.DailyEndTimeUtc;
        rule.MinimumRiskLevel = ParseOptionalRisk(request.MinimumRiskLevel);
        rule.MaximumRiskLevel = ParseOptionalRisk(request.MaximumRiskLevel);
        rule.RequiredAssuranceLevel = ParseAssurance(request.RequiredAssuranceLevel);
        rule.IsActive = request.IsActive;
        rule.UpdatedAt = _clock.UtcNow;
        AddRuleAudit("ACCESS_POLICY_DRAFT_RULE_UPDATED", rule, rule.ApplicationSystem.Code);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RuleFailure(VersionedUpdates.ConflictCode, "The rule changed after it was loaded.");
        }
        catch (DbUpdateException)
        {
            return RuleFailure("POLICY_PRIORITY_TAKEN", "Another rule already uses this priority in the policy version.");
        }

        await PopulateRuleTargetsAsync(rule, ct);
        return OperationResult<AccessPolicyRuleDto>.Success(MapRule(rule));
    }

    public async Task<OperationResult> DeleteAsync(Guid ruleId, CancellationToken ct = default)
    {
        var rule = await BaseRuleQuery(tracking: true).FirstOrDefaultAsync(candidate => candidate.Id == ruleId, ct);
        if (rule is null)
            return OperationResult.Failure("POLICY_RULE_NOT_FOUND", "Access policy rule not found.");
        if (rule.PolicyVersion.Status != AccessPolicyVersionStatus.Draft)
            return OperationResult.Failure("POLICY_VERSION_IMMUTABLE", "Published and archived policy versions are immutable; create a draft first.");

        _db.ApplicationAccessPolicyRules.Remove(rule);
        AddRuleAudit("ACCESS_POLICY_DRAFT_RULE_DELETED", rule, rule.ApplicationSystem.Code);
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public Task<AccessPolicyDecision> EvaluateAsync(
        Guid userId,
        Guid applicationSystemId,
        string? ipAddress,
        CancellationToken ct = default) =>
        EvaluateAsync(new AccessPolicyEvaluationContext(
            userId,
            applicationSystemId,
            ipAddress,
            _clock.UtcNow,
            AccessRiskLevel.Unknown,
            AuthenticationAssuranceLevel.Password), ct);

    public async Task<AccessPolicyDecision> EvaluateAsync(
        AccessPolicyEvaluationContext context,
        CancellationToken ct = default)
    {
        var versionQuery = _db.ApplicationAccessPolicyVersions.AsNoTracking();
        ApplicationAccessPolicyVersion? version;
        if (context.PolicyVersionId.HasValue)
        {
            version = await versionQuery.FirstOrDefaultAsync(candidate =>
                candidate.Id == context.PolicyVersionId &&
                candidate.ApplicationSystemId == context.ApplicationSystemId, ct);
        }
        else
        {
            version = await versionQuery
                .Where(candidate =>
                    candidate.ApplicationSystemId == context.ApplicationSystemId &&
                    candidate.Status == AccessPolicyVersionStatus.Published)
                .OrderByDescending(candidate => candidate.VersionNumber)
                .FirstOrDefaultAsync(ct);
        }

        if (version is null)
            return AccessPolicyDecision.AllowByDefault;

        var rules = await _db.ApplicationAccessPolicyRules
            .AsNoTracking()
            .Where(rule => rule.PolicyVersionId == version.Id && rule.IsActive)
            .OrderBy(rule => rule.Priority)
            .ToListAsync(ct);
        if (rules.Count == 0)
            return EmptyVersionDecision(version);

        var groupIds = await _db.UserGroupMemberships
            .AsNoTracking()
            .Where(membership => membership.UserId == context.UserId && membership.Group.IsActive)
            .Select(membership => membership.GroupId)
            .ToListAsync(ct);
        var groups = groupIds.ToHashSet();
        var address = ParseAddress(context.IpAddress);
        var evaluatedAt = NormalizeUtc(context.EvaluatedAtUtc) ?? _clock.UtcNow;
        var evaluations = new List<AccessPolicyRuleEvaluation>();
        AccessPolicyDecision? matchedDecision = null;

        foreach (var rule in rules)
        {
            var reasons = EvaluateConditions(rule, context, groups, address, evaluatedAt);
            var matched = reasons.Count == 0;
            if (context.IncludeExplanation)
                evaluations.Add(new AccessPolicyRuleEvaluation(rule.Id, rule.Name, rule.Priority, matched, matched ? ["All configured conditions matched."] : reasons));
            if (!matched)
                continue;

            // Ordered policies are decided by the first match. Simulations still evaluate the
            // remaining rules so an administrator can see shadowed matches before publishing.
            if (matchedDecision is not null)
                continue;

            var requiredAssurance = rule.MfaRequirement == AccessPolicyMfaRequirement.Required
                ? AuthenticationAssuranceLevel.Mfa
                : rule.RequiredAssuranceLevel;
            if (rule.RequiredAssuranceLevel > requiredAssurance)
                requiredAssurance = rule.RequiredAssuranceLevel;
            var allowed = rule.Action == AccessPolicyAction.Allow;
            matchedDecision = new AccessPolicyDecision(
                allowed,
                allowed && context.AssuranceLevel < requiredAssurance,
                allowed && rule.AllowTrustedDeviceBypass,
                requiredAssurance,
                rule.Id,
                rule.Name,
                allowed ? "The first matching rule allows access." : "The first matching rule denies access.",
                version.Id,
                version.VersionNumber,
                version.Status,
                evaluations);
            if (!context.IncludeExplanation)
                return matchedDecision;
        }

        if (matchedDecision is not null)
            return matchedDecision with { RuleEvaluations = evaluations };

        return new AccessPolicyDecision(
            false,
            false,
            false,
            AuthenticationAssuranceLevel.Password,
            null,
            null,
            "The policy contains active rules but none matched; deny-by-default applies.",
            version.Id,
            version.VersionNumber,
            version.Status,
            evaluations);
    }

    public async Task<OperationResult<AccessPolicySimulationResponse>> SimulateAsync(
        SimulateAccessPolicyRequest request,
        CancellationToken ct = default)
    {
        if (!Enum.TryParse<AccessRiskLevel>(request.RiskLevel, true, out var risk) || !Enum.IsDefined(risk))
            return SimulationFailure("INVALID_RISK_LEVEL", "RiskLevel must be Unknown, Low, Medium, High, or Critical.");
        if (!Enum.TryParse<AuthenticationAssuranceLevel>(request.AssuranceLevel, true, out var assurance) || !Enum.IsDefined(assurance))
            return SimulationFailure("INVALID_ASSURANCE_LEVEL", "AssuranceLevel must be Password or Mfa.");
        if (!await _db.ApplicationSystems.AnyAsync(application => application.Id == request.ApplicationSystemId, ct))
            return SimulationFailure("APP_NOT_FOUND", "Application not found.");
        if (!await _db.Users.AnyAsync(user => user.Id == request.UserId, ct))
            return SimulationFailure("USER_NOT_FOUND", "User not found.");

        var version = await SelectManagementVersionAsync(request.ApplicationSystemId, request.PolicyVersionId, ct);
        if (request.PolicyVersionId.HasValue && version is null)
            return SimulationFailure("POLICY_VERSION_NOT_FOUND", "Access policy version not found for this application.");

        var context = new AccessPolicyEvaluationContext(
            request.UserId,
            request.ApplicationSystemId,
            request.IpAddress,
            NormalizeUtc(request.EvaluatedAtUtc) ?? _clock.UtcNow,
            risk,
            assurance,
            version?.Id,
            true);
        var decision = await EvaluateAsync(context, ct);
        return OperationResult<AccessPolicySimulationResponse>.Success(new AccessPolicySimulationResponse
        {
            IsAllowed = decision.IsAllowed,
            RequireMfa = decision.RequireMfa,
            AllowTrustedDeviceBypass = decision.AllowTrustedDeviceBypass,
            RequiredAssuranceLevel = decision.RequiredAssuranceLevel.ToString(),
            MatchedRuleId = decision.MatchedRuleId,
            MatchedRuleName = decision.MatchedRuleName,
            DecisionReason = decision.DecisionReason,
            PolicyVersionId = decision.PolicyVersionId,
            PolicyVersionNumber = decision.PolicyVersionNumber,
            PolicyVersionStatus = decision.PolicyVersionStatus?.ToString(),
            RuleEvaluations = decision.RuleEvaluations.Select(evaluation => new AccessPolicyRuleEvaluationDto
            {
                RuleId = evaluation.RuleId,
                RuleName = evaluation.RuleName,
                Priority = evaluation.Priority,
                Matched = evaluation.Matched,
                Reasons = evaluation.Reasons
            }).ToList()
        });
    }

    private IQueryable<ApplicationAccessPolicyRule> BaseRuleQuery(bool tracking = false)
    {
        var query = _db.ApplicationAccessPolicyRules
            .Include(rule => rule.PolicyVersion)
            .Include(rule => rule.ApplicationSystem)
            .Include(rule => rule.User)
            .Include(rule => rule.DirectoryGroup)
            .AsQueryable();
        return tracking ? query : query.AsNoTracking();
    }

    private async Task<ApplicationAccessPolicyVersion?> SelectManagementVersionAsync(
        Guid applicationSystemId,
        Guid? policyVersionId,
        CancellationToken ct)
    {
        var query = _db.ApplicationAccessPolicyVersions.AsNoTracking();
        if (policyVersionId.HasValue)
            return await query.FirstOrDefaultAsync(version =>
                version.Id == policyVersionId && version.ApplicationSystemId == applicationSystemId, ct);

        return await query
            .Where(version => version.ApplicationSystemId == applicationSystemId)
            .OrderBy(version => version.Status == AccessPolicyVersionStatus.Draft ? 0 :
                version.Status == AccessPolicyVersionStatus.Published ? 1 : 2)
            .ThenByDescending(version => version.VersionNumber)
            .FirstOrDefaultAsync(ct);
    }

    private static IReadOnlyList<string> EvaluateConditions(
        ApplicationAccessPolicyRule rule,
        AccessPolicyEvaluationContext context,
        IReadOnlySet<Guid> groups,
        IPAddress? address,
        DateTime evaluatedAtUtc)
    {
        var reasons = new List<string>();
        if (rule.UserId.HasValue && rule.UserId != context.UserId)
            reasons.Add("The user does not match the rule's user condition.");
        if (rule.DirectoryGroupId.HasValue && !groups.Contains(rule.DirectoryGroupId.Value))
            reasons.Add("The user is not an active member of the required group.");

        var included = DeserializeCidrs(rule.IncludedIpCidrsJson);
        if (included.Count > 0 && (address is null || !included.Any(cidr => cidr.Contains(address))))
            reasons.Add(address is null
                ? "No valid caller IP was available for the included network condition."
                : "The caller IP is outside all included network ranges.");
        var excluded = DeserializeCidrs(rule.ExcludedIpCidrsJson);
        if (address is not null && excluded.Any(cidr => cidr.Contains(address)))
            reasons.Add("The caller IP is inside an excluded network range.");

        if (rule.ActiveFromUtc.HasValue && evaluatedAtUtc < rule.ActiveFromUtc.Value)
            reasons.Add("The evaluation time is before the rule's activation time.");
        if (rule.ActiveUntilUtc.HasValue && evaluatedAtUtc >= rule.ActiveUntilUtc.Value)
            reasons.Add("The evaluation time is at or after the rule's expiration time.");
        var activeDays = DeserializeDays(rule.ActiveDaysUtcJson);
        if (activeDays.Count > 0 && !activeDays.Contains(evaluatedAtUtc.DayOfWeek))
            reasons.Add("The UTC day is outside the rule's active days.");
        if (rule.DailyStartTimeUtc.HasValue && rule.DailyEndTimeUtc.HasValue &&
            !IsWithinDailyWindow(TimeOnly.FromDateTime(evaluatedAtUtc), rule.DailyStartTimeUtc.Value, rule.DailyEndTimeUtc.Value))
            reasons.Add("The UTC time is outside the rule's daily active window.");

        if (rule.MinimumRiskLevel.HasValue && context.RiskLevel < rule.MinimumRiskLevel.Value)
            reasons.Add($"Risk {context.RiskLevel} is below the rule minimum {rule.MinimumRiskLevel}.");
        if (rule.MaximumRiskLevel.HasValue && context.RiskLevel > rule.MaximumRiskLevel.Value)
            reasons.Add($"Risk {context.RiskLevel} exceeds the rule maximum {rule.MaximumRiskLevel}.");
        return reasons;
    }

    private static bool IsWithinDailyWindow(TimeOnly current, TimeOnly start, TimeOnly end) =>
        start <= end ? current >= start && current < end : current >= start || current < end;

    private async Task<OperationResult> ValidateTargetsAsync(Guid? userId, Guid? groupId, CancellationToken ct)
    {
        if (userId.HasValue && !await _db.Users.AnyAsync(user => user.Id == userId, ct))
            return OperationResult.Failure("USER_NOT_FOUND", "Active user not found.");
        if (groupId.HasValue && !await _db.DirectoryGroups.AnyAsync(group => group.Id == groupId && group.IsActive, ct))
            return OperationResult.Failure("GROUP_NOT_FOUND", "Active directory group not found.");
        return OperationResult.Success();
    }

    private Task<bool> PriorityExistsAsync(Guid versionId, int priority, Guid? excludedId, CancellationToken ct) =>
        _db.ApplicationAccessPolicyRules.AnyAsync(
            rule => rule.PolicyVersionId == versionId && rule.Priority == priority && rule.Id != excludedId,
            ct);

    private async Task PopulateRuleTargetsAsync(ApplicationAccessPolicyRule rule, CancellationToken ct)
    {
        rule.User = rule.UserId.HasValue
            ? await _db.Users.AsNoTracking().FirstAsync(user => user.Id == rule.UserId, ct)
            : null;
        rule.DirectoryGroup = rule.DirectoryGroupId.HasValue
            ? await _db.DirectoryGroups.AsNoTracking().FirstAsync(group => group.Id == rule.DirectoryGroupId, ct)
            : null;
    }

    private static ApplicationAccessPolicyRule CloneRule(
        ApplicationAccessPolicyRule source,
        ApplicationAccessPolicyVersion draft) => new()
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = draft.Id,
            ApplicationSystemId = draft.ApplicationSystemId,
            UserId = source.UserId,
            DirectoryGroupId = source.DirectoryGroupId,
            Name = source.Name,
            Priority = source.Priority,
            Action = source.Action,
            MfaRequirement = source.MfaRequirement,
            AllowTrustedDeviceBypass = source.AllowTrustedDeviceBypass,
            IncludedIpCidrsJson = source.IncludedIpCidrsJson,
            ExcludedIpCidrsJson = source.ExcludedIpCidrsJson,
            ActiveFromUtc = source.ActiveFromUtc,
            ActiveUntilUtc = source.ActiveUntilUtc,
            ActiveDaysUtcJson = source.ActiveDaysUtcJson,
            DailyStartTimeUtc = source.DailyStartTimeUtc,
            DailyEndTimeUtc = source.DailyEndTimeUtc,
            MinimumRiskLevel = source.MinimumRiskLevel,
            MaximumRiskLevel = source.MaximumRiskLevel,
            RequiredAssuranceLevel = source.RequiredAssuranceLevel,
            IsActive = source.IsActive,
            CreatedAt = draft.CreatedAt,
            PolicyVersion = draft,
            ApplicationSystem = draft.ApplicationSystem
        };

    private async Task SaveAndRevokeApplicationSessionsAsync(string applicationCode, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (_db.Database.IsRelational())
        {
            var pendingStates = _db.ChangeTracker.Entries()
                .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Select(entry => (Entry: entry, State: entry.State))
                .ToList();
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                foreach (var (entry, state) in pendingStates)
                    entry.State = state;

                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                await _db.SaveChangesAsync(ct);
                await _db.RefreshTokens
                    .Where(token => token.ApplicationCode == applicationCode && token.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), ct);
                await transaction.CommitAsync(ct);
            });
            return;
        }

        await _db.SaveChangesAsync(ct);
        var tokens = await _db.RefreshTokens
            .Where(token => token.ApplicationCode == applicationCode && token.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in tokens)
            token.RevokedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    private void AddRuleAudit(string action, ApplicationAccessPolicyRule rule, string applicationCode)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = _currentUser.UserId,
            ApplicationCode = applicationCode,
            Action = action,
            EntityName = nameof(ApplicationAccessPolicyRule),
            EntityId = rule.Id.ToString(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                rule.PolicyVersionId,
                rule.Name,
                rule.Priority,
                action = rule.Action.ToString(),
                mfa = rule.MfaRequirement.ToString(),
                assurance = rule.RequiredAssuranceLevel.ToString(),
                rule.UserId,
                rule.DirectoryGroupId,
                rule.IsActive
            }),
            CreatedAt = _clock.UtcNow
        });
    }

    private void AddVersionAudit(string action, ApplicationAccessPolicyVersion version, string applicationCode)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = _currentUser.UserId,
            ApplicationCode = applicationCode,
            Action = action,
            EntityName = nameof(ApplicationAccessPolicyVersion),
            EntityId = version.Id.ToString(),
            MetadataJson = JsonSerializer.Serialize(new { version.VersionNumber, status = version.Status.ToString() }),
            CreatedAt = _clock.UtcNow
        });
    }

    private static RuleValidation ValidateRule(
        string name,
        int priority,
        string action,
        string mfaRequirement,
        IReadOnlyList<string>? includedCidrs,
        IReadOnlyList<string>? excludedCidrs,
        DateTime? activeFromUtc,
        DateTime? activeUntilUtc,
        IReadOnlyList<string>? activeDaysUtc,
        TimeOnly? dailyStartTimeUtc,
        TimeOnly? dailyEndTimeUtc,
        string? minimumRiskLevel,
        string? maximumRiskLevel,
        string requiredAssuranceLevel)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            return RuleValidation.Failure("INVALID_POLICY_NAME", "Rule name is required and must not exceed 200 characters.");
        if (priority is < 1 or > 10000)
            return RuleValidation.Failure("INVALID_POLICY_PRIORITY", "Priority must be between 1 and 10000.");
        if (!Enum.TryParse<AccessPolicyAction>(action, true, out var parsedAction) || !Enum.IsDefined(parsedAction))
            return RuleValidation.Failure("INVALID_POLICY_ACTION", "Action must be Allow or Deny.");
        if (!Enum.TryParse<AccessPolicyMfaRequirement>(mfaRequirement, true, out var parsedMfa) || !Enum.IsDefined(parsedMfa))
            return RuleValidation.Failure("INVALID_MFA_REQUIREMENT", "MfaRequirement must be Optional or Required.");
        if (!Enum.TryParse<AuthenticationAssuranceLevel>(requiredAssuranceLevel, true, out var assurance) || !Enum.IsDefined(assurance))
            return RuleValidation.Failure("INVALID_ASSURANCE_LEVEL", "RequiredAssuranceLevel must be Password, Mfa, or PhishingResistant.");

        var allLists = new[] { includedCidrs ?? [], excludedCidrs ?? [] };
        if (allLists.Any(list => list.Count > MaxCidrsPerCondition))
            return RuleValidation.Failure("TOO_MANY_IP_RANGES", $"Each IP condition supports at most {MaxCidrsPerCondition} CIDR ranges.");
        if (allLists.SelectMany(list => list).Any(cidr => !IpCidr.TryParse(cidr, out _)))
            return RuleValidation.Failure("INVALID_IP_RANGE", "IP ranges must be valid IPv4 or IPv6 CIDR values.");

        if (activeFromUtc.HasValue && activeUntilUtc.HasValue && NormalizeUtc(activeFromUtc) >= NormalizeUtc(activeUntilUtc))
            return RuleValidation.Failure("INVALID_POLICY_SCHEDULE", "ActiveFromUtc must be earlier than ActiveUntilUtc.");
        if (dailyStartTimeUtc.HasValue != dailyEndTimeUtc.HasValue)
            return RuleValidation.Failure("INVALID_POLICY_DAILY_WINDOW", "DailyStartTimeUtc and DailyEndTimeUtc must be configured together.");
        if (dailyStartTimeUtc.HasValue && dailyStartTimeUtc == dailyEndTimeUtc)
            return RuleValidation.Failure("INVALID_POLICY_DAILY_WINDOW", "DailyStartTimeUtc and DailyEndTimeUtc cannot be equal; omit both values for an all-day rule.");
        if ((activeDaysUtc ?? []).Any(day => !Enum.TryParse<DayOfWeek>(day, true, out _)))
            return RuleValidation.Failure("INVALID_POLICY_ACTIVE_DAY", "ActiveDaysUtc must contain valid English day names.");

        if (!TryParseOptionalRisk(minimumRiskLevel, out var minimumRisk) || !TryParseOptionalRisk(maximumRiskLevel, out var maximumRisk))
            return RuleValidation.Failure("INVALID_RISK_LEVEL", "Risk levels must be Unknown, Low, Medium, High, or Critical.");
        if (minimumRisk.HasValue && maximumRisk.HasValue && minimumRisk > maximumRisk)
            return RuleValidation.Failure("INVALID_RISK_RANGE", "MinimumRiskLevel cannot exceed MaximumRiskLevel.");
        return RuleValidation.Success();
    }

    private static bool IsUnconditionalAllow(ApplicationAccessPolicyRule rule) =>
        rule.IsActive &&
        rule.Action == AccessPolicyAction.Allow &&
        rule.UserId is null &&
        rule.DirectoryGroupId is null &&
        rule.IncludedIpCidrsJson is null &&
        rule.ExcludedIpCidrsJson is null &&
        rule.ActiveFromUtc is null &&
        rule.ActiveUntilUtc is null &&
        rule.ActiveDaysUtcJson is null &&
        rule.DailyStartTimeUtc is null &&
        rule.DailyEndTimeUtc is null &&
        rule.MinimumRiskLevel is null &&
        rule.MaximumRiskLevel is null;

    private static AccessPolicyDecision EmptyVersionDecision(ApplicationAccessPolicyVersion version) => new(
        true,
        false,
        true,
        AuthenticationAssuranceLevel.Password,
        null,
        null,
        "The selected policy version has no active rules; access is allowed by default.",
        version.Id,
        version.VersionNumber,
        version.Status,
        []);

    private static AccessPolicyAction ParseAction(string value) => Enum.Parse<AccessPolicyAction>(value, true);
    private static AccessPolicyMfaRequirement ParseMfa(string value) => Enum.Parse<AccessPolicyMfaRequirement>(value, true);
    private static AuthenticationAssuranceLevel ParseAssurance(string value) => Enum.Parse<AuthenticationAssuranceLevel>(value, true);
    private static AccessRiskLevel? ParseOptionalRisk(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Enum.Parse<AccessRiskLevel>(value, true);

    private static bool TryParseOptionalRisk(string? value, out AccessRiskLevel? risk)
    {
        risk = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (!Enum.TryParse<AccessRiskLevel>(value, true, out var parsed) || !Enum.IsDefined(parsed))
            return false;
        risk = parsed;
        return true;
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue)
            return null;
        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    private static string? SerializeStrings(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return null;
        return JsonSerializer.Serialize(values.Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? SerializeDays(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return null;
        return JsonSerializer.Serialize(values
            .Select(value => Enum.Parse<DayOfWeek>(value, true).ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlySet<DayOfWeek> DeserializeDays(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new HashSet<DayOfWeek>()
            : (JsonSerializer.Deserialize<string[]>(json) ?? [])
                .Select(value => Enum.Parse<DayOfWeek>(value, true))
                .ToHashSet();

    private static IReadOnlyList<IpCidr> DeserializeCidrs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        return (JsonSerializer.Deserialize<string[]>(json) ?? [])
            .Select(value => IpCidr.TryParse(value, out var cidr) ? cidr : null)
            .Where(cidr => cidr is not null)
            .Cast<IpCidr>()
            .ToList();
    }

    private static IReadOnlyList<string> DeserializeStrings(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static IPAddress? ParseAddress(string? value)
    {
        if (!IPAddress.TryParse(value, out var address))
            return null;
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private static AccessPolicyRuleDto MapRule(ApplicationAccessPolicyRule rule) => new()
    {
        Version = rule.Version,
        Id = rule.Id,
        ApplicationSystemId = rule.ApplicationSystemId,
        PolicyVersionId = rule.PolicyVersionId,
        PolicyVersionNumber = rule.PolicyVersion.VersionNumber,
        PolicyVersionStatus = rule.PolicyVersion.Status.ToString(),
        ApplicationCode = rule.ApplicationSystem.Code,
        UserId = rule.UserId,
        UserEmail = rule.User?.Email,
        DirectoryGroupId = rule.DirectoryGroupId,
        DirectoryGroupName = rule.DirectoryGroup?.Name,
        Name = rule.Name,
        Priority = rule.Priority,
        Action = rule.Action.ToString(),
        MfaRequirement = rule.MfaRequirement.ToString(),
        AllowTrustedDeviceBypass = rule.AllowTrustedDeviceBypass,
        IncludedIpCidrs = DeserializeCidrs(rule.IncludedIpCidrsJson).Select(cidr => cidr.ToString()).ToList(),
        ExcludedIpCidrs = DeserializeCidrs(rule.ExcludedIpCidrsJson).Select(cidr => cidr.ToString()).ToList(),
        ActiveFromUtc = rule.ActiveFromUtc,
        ActiveUntilUtc = rule.ActiveUntilUtc,
        ActiveDaysUtc = DeserializeStrings(rule.ActiveDaysUtcJson),
        DailyStartTimeUtc = rule.DailyStartTimeUtc,
        DailyEndTimeUtc = rule.DailyEndTimeUtc,
        MinimumRiskLevel = rule.MinimumRiskLevel?.ToString(),
        MaximumRiskLevel = rule.MaximumRiskLevel?.ToString(),
        RequiredAssuranceLevel = rule.RequiredAssuranceLevel.ToString(),
        IsActive = rule.IsActive,
        CreatedAt = rule.CreatedAt,
        UpdatedAt = rule.UpdatedAt
    };

    private static AccessPolicyVersionDto MapVersion(ApplicationAccessPolicyVersion version) => new()
    {
        Id = version.Id,
        ApplicationSystemId = version.ApplicationSystemId,
        VersionNumber = version.VersionNumber,
        Status = version.Status.ToString(),
        RuleCount = version.Rules.Count,
        CreatedAt = version.CreatedAt,
        PublishedAt = version.PublishedAt
    };

    private static OperationResult<AccessPolicyRuleDto> RuleFailure(string? code, string? message) =>
        OperationResult<AccessPolicyRuleDto>.Failure(code ?? "INVALID_POLICY_RULE", message ?? "The policy rule is invalid.");
    private static OperationResult<AccessPolicyVersionDto> VersionFailure(string code, string message) =>
        OperationResult<AccessPolicyVersionDto>.Failure(code, message);
    private static OperationResult<AccessPolicySimulationResponse> SimulationFailure(string code, string message) =>
        OperationResult<AccessPolicySimulationResponse>.Failure(code, message);

    private sealed record RuleValidation(bool IsSuccess, string? ErrorCode, string? Message)
    {
        public static RuleValidation Success() => new(true, null, null);
        public static RuleValidation Failure(string code, string message) => new(false, code, message);
    }

    private sealed class IpCidr
    {
        private readonly IPAddress _network;
        private readonly int _prefixLength;

        private IpCidr(IPAddress network, int prefixLength)
        {
            _network = network;
            _prefixLength = prefixLength;
        }

        public static bool TryParse(string? value, out IpCidr? cidr)
        {
            cidr = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var parts = value.Trim().Split('/', 2);
            if (!IPAddress.TryParse(parts[0], out var address))
                return false;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            var maxBits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var prefix = maxBits;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > maxBits))
                return false;

            cidr = new IpCidr(address, prefix);
            return true;
        }

        public bool Contains(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (address.AddressFamily != _network.AddressFamily)
                return false;

            var candidate = address.GetAddressBytes();
            var network = _network.GetAddressBytes();
            var completeBytes = _prefixLength / 8;
            var remainingBits = _prefixLength % 8;
            for (var index = 0; index < completeBytes; index++)
            {
                if (candidate[index] != network[index])
                    return false;
            }

            if (remainingBits == 0)
                return true;
            var mask = (byte)(0xFF << (8 - remainingBits));
            return (candidate[completeBytes] & mask) == (network[completeBytes] & mask);
        }

        public override string ToString() => $"{_network}/{_prefixLength}";
    }
}
