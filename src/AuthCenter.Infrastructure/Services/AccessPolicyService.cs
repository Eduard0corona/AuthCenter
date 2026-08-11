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

    public AccessPolicyService(
        AuthCenterDbContext db,
        IDateTimeProvider clock,
        ICurrentUserService currentUser)
    {
        _db = db;
        _clock = clock;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<AccessPolicyRuleDto>> GetByApplicationAsync(
        Guid applicationSystemId,
        CancellationToken ct = default)
    {
        var rules = await BaseQuery()
            .Where(rule => rule.ApplicationSystemId == applicationSystemId)
            .OrderBy(rule => rule.Priority)
            .ToListAsync(ct);
        return rules.Select(Map).ToList();
    }

    public async Task<OperationResult<AccessPolicyRuleDto>> CreateAsync(
        CreateAccessPolicyRuleRequest request,
        CancellationToken ct = default)
    {
        var validation = Validate(
            request.Name,
            request.Priority,
            request.Action,
            request.MfaRequirement,
            request.IncludedIpCidrs,
            request.ExcludedIpCidrs);
        if (!validation.IsSuccess)
            return OperationResult<AccessPolicyRuleDto>.Failure(validation.ErrorCode, validation.Message);

        var application = await _db.ApplicationSystems.FindAsync([request.ApplicationSystemId], ct);
        if (application is null)
            return OperationResult<AccessPolicyRuleDto>.Failure("APP_NOT_FOUND", "Application not found.");

        if (request.DirectoryGroupId.HasValue &&
            !await _db.DirectoryGroups.AnyAsync(group => group.Id == request.DirectoryGroupId && group.IsActive, ct))
            return OperationResult<AccessPolicyRuleDto>.Failure("GROUP_NOT_FOUND", "Active directory group not found.");

        if (await PriorityExistsAsync(request.ApplicationSystemId, request.Priority, null, ct))
            return OperationResult<AccessPolicyRuleDto>.Failure("POLICY_PRIORITY_TAKEN", "Another rule already uses this priority for the application.");

        if (application.Code == DomainConstants.SystemCodes.AuthCenter && request.IsActive &&
            !IsCatchAllAllow(request.Action, request.DirectoryGroupId, request.IncludedIpCidrs, request.ExcludedIpCidrs) &&
            !await HasActiveCatchAllAllowAsync(application.Id, null, ct))
            return OperationResult<AccessPolicyRuleDto>.Failure(
                "AUTHCENTER_POLICY_FALLBACK_REQUIRED",
                "Create an active unconditional Allow fallback before adding restrictive AuthCenter rules.");

        var now = _clock.UtcNow;
        var rule = new ApplicationAccessPolicyRule
        {
            Id = Guid.NewGuid(),
            ApplicationSystemId = request.ApplicationSystemId,
            DirectoryGroupId = request.DirectoryGroupId,
            Name = request.Name.Trim(),
            Priority = request.Priority,
            Action = ParseAction(request.Action),
            MfaRequirement = ParseMfa(request.MfaRequirement),
            AllowTrustedDeviceBypass = request.AllowTrustedDeviceBypass,
            IncludedIpCidrsJson = SerializeCidrs(request.IncludedIpCidrs),
            ExcludedIpCidrsJson = SerializeCidrs(request.ExcludedIpCidrs),
            IsActive = request.IsActive,
            CreatedAt = now,
            ApplicationSystem = application
        };
        _db.ApplicationAccessPolicyRules.Add(rule);
        AddAudit("ACCESS_POLICY_RULE_CREATED", rule, application.Code);

        try
        {
            await SaveAndRevokeApplicationSessionsAsync(application.Code, ct);
        }
        catch (DbUpdateException)
        {
            return OperationResult<AccessPolicyRuleDto>.Failure("POLICY_PRIORITY_TAKEN", "Another rule already uses this priority for the application.");
        }

        rule.DirectoryGroup = request.DirectoryGroupId.HasValue
            ? await _db.DirectoryGroups.AsNoTracking().FirstAsync(group => group.Id == request.DirectoryGroupId, ct)
            : null;
        return OperationResult<AccessPolicyRuleDto>.Success(Map(rule));
    }

    public async Task<OperationResult<AccessPolicyRuleDto>> UpdateAsync(
        Guid ruleId,
        UpdateAccessPolicyRuleRequest request,
        CancellationToken ct = default)
    {
        var validation = Validate(
            request.Name,
            request.Priority,
            request.Action,
            request.MfaRequirement,
            request.IncludedIpCidrs,
            request.ExcludedIpCidrs);
        if (!validation.IsSuccess)
            return OperationResult<AccessPolicyRuleDto>.Failure(validation.ErrorCode, validation.Message);

        var rule = await BaseQuery(tracking: true).FirstOrDefaultAsync(candidate => candidate.Id == ruleId, ct);
        if (rule is null)
            return OperationResult<AccessPolicyRuleDto>.Failure("POLICY_RULE_NOT_FOUND", "Access policy rule not found.");

        if (request.DirectoryGroupId.HasValue &&
            !await _db.DirectoryGroups.AnyAsync(group => group.Id == request.DirectoryGroupId && group.IsActive, ct))
            return OperationResult<AccessPolicyRuleDto>.Failure("GROUP_NOT_FOUND", "Active directory group not found.");

        if (await PriorityExistsAsync(rule.ApplicationSystemId, request.Priority, rule.Id, ct))
            return OperationResult<AccessPolicyRuleDto>.Failure("POLICY_PRIORITY_TAKEN", "Another rule already uses this priority for the application.");

        if (rule.ApplicationSystem.Code == DomainConstants.SystemCodes.AuthCenter &&
            !(request.IsActive && IsCatchAllAllow(request.Action, request.DirectoryGroupId, request.IncludedIpCidrs, request.ExcludedIpCidrs)) &&
            !await HasActiveCatchAllAllowAsync(rule.ApplicationSystemId, rule.Id, ct))
            return OperationResult<AccessPolicyRuleDto>.Failure(
                "AUTHCENTER_POLICY_FALLBACK_REQUIRED",
                "AuthCenter must retain an active unconditional Allow fallback to prevent administrative lockout.");

        rule.DirectoryGroupId = request.DirectoryGroupId;
        rule.Name = request.Name.Trim();
        rule.Priority = request.Priority;
        rule.Action = ParseAction(request.Action);
        rule.MfaRequirement = ParseMfa(request.MfaRequirement);
        rule.AllowTrustedDeviceBypass = request.AllowTrustedDeviceBypass;
        rule.IncludedIpCidrsJson = SerializeCidrs(request.IncludedIpCidrs);
        rule.ExcludedIpCidrsJson = SerializeCidrs(request.ExcludedIpCidrs);
        rule.IsActive = request.IsActive;
        rule.UpdatedAt = _clock.UtcNow;
        AddAudit("ACCESS_POLICY_RULE_UPDATED", rule, rule.ApplicationSystem.Code);

        try
        {
            await SaveAndRevokeApplicationSessionsAsync(rule.ApplicationSystem.Code, ct);
        }
        catch (DbUpdateException)
        {
            return OperationResult<AccessPolicyRuleDto>.Failure("POLICY_PRIORITY_TAKEN", "Another rule already uses this priority for the application.");
        }

        rule.DirectoryGroup = request.DirectoryGroupId.HasValue
            ? await _db.DirectoryGroups.AsNoTracking().FirstAsync(group => group.Id == request.DirectoryGroupId, ct)
            : null;
        return OperationResult<AccessPolicyRuleDto>.Success(Map(rule));
    }

    public async Task<OperationResult> DeleteAsync(Guid ruleId, CancellationToken ct = default)
    {
        var rule = await BaseQuery(tracking: true).FirstOrDefaultAsync(candidate => candidate.Id == ruleId, ct);
        if (rule is null)
            return OperationResult.Failure("POLICY_RULE_NOT_FOUND", "Access policy rule not found.");

        var applicationCode = rule.ApplicationSystem.Code;
        if (applicationCode == DomainConstants.SystemCodes.AuthCenter && rule.IsActive &&
            IsCatchAllAllow(rule.Action.ToString(), rule.DirectoryGroupId, DeserializeStrings(rule.IncludedIpCidrsJson), DeserializeStrings(rule.ExcludedIpCidrsJson)) &&
            !await HasActiveCatchAllAllowAsync(rule.ApplicationSystemId, rule.Id, ct))
            return OperationResult.Failure(
                "AUTHCENTER_POLICY_FALLBACK_REQUIRED",
                "AuthCenter must retain an active unconditional Allow fallback to prevent administrative lockout.");

        _db.ApplicationAccessPolicyRules.Remove(rule);
        AddAudit("ACCESS_POLICY_RULE_DELETED", rule, applicationCode);
        await SaveAndRevokeApplicationSessionsAsync(applicationCode, ct);
        return OperationResult.Success();
    }

    public async Task<AccessPolicyDecision> EvaluateAsync(
        Guid userId,
        Guid applicationSystemId,
        string? ipAddress,
        CancellationToken ct = default)
    {
        var rules = await _db.ApplicationAccessPolicyRules
            .AsNoTracking()
            .Where(rule => rule.ApplicationSystemId == applicationSystemId && rule.IsActive)
            .OrderBy(rule => rule.Priority)
            .ToListAsync(ct);

        if (rules.Count == 0)
            return AccessPolicyDecision.AllowByDefault;

        var groupIds = await _db.UserGroupMemberships
            .AsNoTracking()
            .Where(membership => membership.UserId == userId && membership.Group.IsActive)
            .Select(membership => membership.GroupId)
            .ToListAsync(ct);
        var groups = groupIds.ToHashSet();
        var address = ParseAddress(ipAddress);

        foreach (var rule in rules)
        {
            if (rule.DirectoryGroupId.HasValue && !groups.Contains(rule.DirectoryGroupId.Value))
                continue;

            var included = DeserializeCidrs(rule.IncludedIpCidrsJson);
            if (included.Count > 0 && (address is null || !included.Any(cidr => cidr.Contains(address))))
                continue;

            var excluded = DeserializeCidrs(rule.ExcludedIpCidrsJson);
            if (address is not null && excluded.Any(cidr => cidr.Contains(address)))
                continue;

            return new AccessPolicyDecision(
                rule.Action == AccessPolicyAction.Allow,
                rule.MfaRequirement == AccessPolicyMfaRequirement.Required,
                rule.AllowTrustedDeviceBypass,
                rule.Id,
                rule.Name);
        }

        // Once an application has active rules, access is fail-closed unless one matches.
        return new AccessPolicyDecision(false, false, false, null, null);
    }

    private IQueryable<ApplicationAccessPolicyRule> BaseQuery(bool tracking = false)
    {
        var query = _db.ApplicationAccessPolicyRules
            .Include(rule => rule.ApplicationSystem)
            .Include(rule => rule.DirectoryGroup)
            .AsQueryable();
        return tracking ? query : query.AsNoTracking();
    }

    private Task<bool> PriorityExistsAsync(Guid applicationSystemId, int priority, Guid? excludedId, CancellationToken ct) =>
        _db.ApplicationAccessPolicyRules.AnyAsync(
            rule => rule.ApplicationSystemId == applicationSystemId && rule.Priority == priority && rule.Id != excludedId,
            ct);

    private Task<bool> HasActiveCatchAllAllowAsync(Guid applicationSystemId, Guid? excludedId, CancellationToken ct) =>
        _db.ApplicationAccessPolicyRules.AnyAsync(rule =>
            rule.ApplicationSystemId == applicationSystemId &&
            rule.Id != excludedId &&
            rule.IsActive &&
            rule.Action == AccessPolicyAction.Allow &&
            rule.DirectoryGroupId == null &&
            rule.IncludedIpCidrsJson == null &&
            rule.ExcludedIpCidrsJson == null,
            ct);

    private static bool IsCatchAllAllow(
        string action,
        Guid? directoryGroupId,
        IReadOnlyList<string>? includedCidrs,
        IReadOnlyList<string>? excludedCidrs) =>
        string.Equals(action, nameof(AccessPolicyAction.Allow), StringComparison.OrdinalIgnoreCase) &&
        directoryGroupId is null &&
        (includedCidrs is null || includedCidrs.Count == 0) &&
        (excludedCidrs is null || excludedCidrs.Count == 0);

    private async Task SaveAndRevokeApplicationSessionsAsync(string applicationCode, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (_db.Database.IsRelational())
        {
            // SaveChanges accepts changes before the transaction commits. If a transient SQL fault
            // rolls the transaction back, the execution strategy retries the delegate, so restore
            // the intended states at the start of every attempt instead of silently retrying only
            // the session revocation.
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

    private void AddAudit(string action, ApplicationAccessPolicyRule rule, string applicationCode)
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
                rule.Name,
                rule.Priority,
                action = rule.Action.ToString(),
                mfa = rule.MfaRequirement.ToString(),
                rule.DirectoryGroupId,
                rule.IsActive
            }),
            CreatedAt = _clock.UtcNow
        });
    }

    private static OperationResult Validate(
        string name,
        int priority,
        string action,
        string mfaRequirement,
        IReadOnlyList<string>? includedCidrs,
        IReadOnlyList<string>? excludedCidrs)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            return OperationResult.Failure("INVALID_POLICY_NAME", "Rule name is required and must not exceed 200 characters.");
        if (priority is < 1 or > 10000)
            return OperationResult.Failure("INVALID_POLICY_PRIORITY", "Priority must be between 1 and 10000.");
        if (!Enum.TryParse<AccessPolicyAction>(action, true, out _))
            return OperationResult.Failure("INVALID_POLICY_ACTION", "Action must be Allow or Deny.");
        if (!Enum.TryParse<AccessPolicyMfaRequirement>(mfaRequirement, true, out _))
            return OperationResult.Failure("INVALID_MFA_REQUIREMENT", "MfaRequirement must be Optional or Required.");

        var allLists = new[] { includedCidrs ?? [], excludedCidrs ?? [] };
        if (allLists.Any(list => list.Count > MaxCidrsPerCondition))
            return OperationResult.Failure("TOO_MANY_IP_RANGES", $"Each IP condition supports at most {MaxCidrsPerCondition} CIDR ranges.");
        if (allLists.SelectMany(list => list).Any(cidr => !IpCidr.TryParse(cidr, out _)))
            return OperationResult.Failure("INVALID_IP_RANGE", "IP ranges must be valid IPv4 or IPv6 CIDR values.");

        return OperationResult.Success();
    }

    private static AccessPolicyAction ParseAction(string value) =>
        Enum.Parse<AccessPolicyAction>(value, true);

    private static AccessPolicyMfaRequirement ParseMfa(string value) =>
        Enum.Parse<AccessPolicyMfaRequirement>(value, true);

    private static string? SerializeCidrs(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return null;
        return JsonSerializer.Serialize(values.Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
    }

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

    private static AccessPolicyRuleDto Map(ApplicationAccessPolicyRule rule) => new()
    {
        Id = rule.Id,
        ApplicationSystemId = rule.ApplicationSystemId,
        ApplicationCode = rule.ApplicationSystem.Code,
        DirectoryGroupId = rule.DirectoryGroupId,
        DirectoryGroupName = rule.DirectoryGroup?.Name,
        Name = rule.Name,
        Priority = rule.Priority,
        Action = rule.Action.ToString(),
        MfaRequirement = rule.MfaRequirement.ToString(),
        AllowTrustedDeviceBypass = rule.AllowTrustedDeviceBypass,
        IncludedIpCidrs = DeserializeCidrs(rule.IncludedIpCidrsJson).Select(cidr => cidr.ToString()).ToList(),
        ExcludedIpCidrs = DeserializeCidrs(rule.ExcludedIpCidrsJson).Select(cidr => cidr.ToString()).ToList(),
        IsActive = rule.IsActive,
        CreatedAt = rule.CreatedAt,
        UpdatedAt = rule.UpdatedAt
    };

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
