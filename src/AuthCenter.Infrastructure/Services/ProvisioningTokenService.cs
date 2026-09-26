using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses.Lifecycle;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public sealed class ProvisioningTokenService : IProvisioningTokenService
{
    public static readonly IReadOnlySet<string> AllowedScopes = new HashSet<string>(StringComparer.Ordinal) { "scim.users.read", "scim.users.write", "scim.groups.read", "scim.groups.write" };
    private const int RecentFailureKinds = 10;
    private readonly AuthCenterDbContext _db; private readonly IDbContextFactory<AuthCenterDbContext> _dbFactory; private readonly IDateTimeProvider _clock; private readonly IAuditService _audit;
    public ProvisioningTokenService(AuthCenterDbContext db, IDbContextFactory<AuthCenterDbContext> dbFactory, IDateTimeProvider clock, IAuditService audit) { _db = db; _dbFactory = dbFactory; _clock = clock; _audit = audit; }

    public async Task<PagedResult<ProvisioningTokenMetadataDto>> GetAsync(ProvisioningTokenQuery query, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var tokens = _db.ProvisioningTokens.AsNoTracking().Include(item => item.ApplicationSystem).AsQueryable();
        if (query.ApplicationSystemId.HasValue) tokens = tokens.Where(item => item.ApplicationSystemId == query.ApplicationSystemId);
        tokens = query.Status?.Trim().ToLowerInvariant() switch
        {
            "active" => tokens.Where(item => item.RevokedAt == null && item.ExpiresAt > now),
            "expired" => tokens.Where(item => item.RevokedAt == null && item.ExpiresAt <= now),
            "revoked" => tokens.Where(item => item.RevokedAt != null),
            _ => tokens
        };
        var total = await tokens.CountAsync(ct);
        var items = await tokens.OrderByDescending(item => item.CreatedAt).Skip(query.Skip).Take(query.PageSize).ToListAsync(ct);
        return PagedResult<ProvisioningTokenMetadataDto>.Create(items.Select(item => Map(item, now)).ToList(), total, query.Page, query.PageSize);
    }

    public async Task<ProvisioningTokenMetadataDto?> GetByIdAsync(Guid tokenId, CancellationToken ct = default)
    {
        var item = await _db.ProvisioningTokens.AsNoTracking().Include(token => token.ApplicationSystem).SingleOrDefaultAsync(token => token.Id == tokenId, ct);
        return item is null ? null : Map(item, _clock.UtcNow);
    }

    public async Task<OperationResult<ProvisioningTokenResponse>> CreateAsync(CreateProvisioningTokenRequest request, CancellationToken ct = default)
    {
        var scopes = request.Scopes.Distinct(StringComparer.Ordinal).ToArray();
        if (string.IsNullOrWhiteSpace(request.Name) || scopes.Length == 0 || scopes.Any(scope => !AllowedScopes.Contains(scope)) || request.ExpiresAt <= _clock.UtcNow || request.ExpiresAt > _clock.UtcNow.AddDays(366))
            return OperationResult<ProvisioningTokenResponse>.Failure("INVALID_PROVISIONING_TOKEN", "Name, supported scopes, and expiration within one year are required.");
        if (!await _db.ApplicationSystems.AnyAsync(item => item.Id == request.ApplicationSystemId && item.IsActive, ct)) return OperationResult<ProvisioningTokenResponse>.Failure("APP_NOT_FOUND", "Active application not found.");
        return OperationResult<ProvisioningTokenResponse>.Success(await CreateStoredAsync(request.ApplicationSystemId, request.Name.Trim(), scopes, request.ExpiresAt.ToUniversalTime(), ct));
    }

    public async Task<OperationResult<ProvisioningTokenResponse>> RotateAsync(Guid tokenId, DateTime expiresAt, CancellationToken ct = default)
    {
        var old = await _db.ProvisioningTokens.Include(item => item.ApplicationSystem).SingleOrDefaultAsync(item => item.Id == tokenId && item.RevokedAt == null, ct);
        if (old is null) return OperationResult<ProvisioningTokenResponse>.Failure("PROVISIONING_TOKEN_NOT_FOUND", "Active token not found.");
        if (expiresAt <= _clock.UtcNow || expiresAt > _clock.UtcNow.AddDays(366)) return OperationResult<ProvisioningTokenResponse>.Failure("INVALID_TOKEN_EXPIRATION", "Expiration must be within one year.");
        old.RevokedAt = _clock.UtcNow;
        var scopes = JsonSerializer.Deserialize<string[]>(old.ScopesJson) ?? [];
        var result = await CreateStoredAsync(old.ApplicationSystemId, old.Name, scopes, expiresAt.ToUniversalTime(), ct);
        await _audit.LogAsync("PROVISIONING_TOKEN_ROTATED", applicationCode: old.ApplicationSystem.Code, entityName: nameof(ProvisioningToken), entityId: tokenId.ToString(), metadata: new { result = "Success", replacementId = result.Id, expiresAt }, ct: ct);
        return OperationResult<ProvisioningTokenResponse>.Success(result);
    }

    public async Task<OperationResult> RevokeAsync(Guid tokenId, CancellationToken ct = default)
    {
        var token = await _db.ProvisioningTokens.Include(item => item.ApplicationSystem).SingleOrDefaultAsync(item => item.Id == tokenId && item.RevokedAt == null, ct);
        if (token is null) return OperationResult.Failure("PROVISIONING_TOKEN_NOT_FOUND", "Active token not found.");
        token.RevokedAt = _clock.UtcNow; await _db.SaveChangesAsync(ct); await _audit.LogAsync("PROVISIONING_TOKEN_REVOKED", applicationCode: token.ApplicationSystem.Code, entityName: nameof(ProvisioningToken), entityId: tokenId.ToString(), metadata: new { result = "Success" }, ct: ct); return OperationResult.Success();
    }

    public async Task<ProvisioningTokenCheck> AuthenticateAsync(string? rawToken, string requiredScope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return new(null, null, ProvisioningTokenCheck.Missing);
        if (!rawToken.StartsWith("acp_", StringComparison.Ordinal) || !AllowedScopes.Contains(requiredScope))
            return new(null, null, ProvisioningTokenCheck.Unknown);
        var hash = Hash(rawToken); var now = _clock.UtcNow;
        var token = await _db.ProvisioningTokens.Include(item => item.ApplicationSystem).SingleOrDefaultAsync(item => item.TokenHash == hash, ct);
        if (token is null)
            return new(null, null, ProvisioningTokenCheck.Unknown);
        // The token is known from here on: a refusal is recorded for its diagnostics.
        if (token.RevokedAt.HasValue)
            return new(null, token.Id, ProvisioningTokenCheck.Revoked);
        if (token.ExpiresAt <= now)
            return new(null, token.Id, ProvisioningTokenCheck.Expired);
        if (!token.ApplicationSystem.IsActive)
            return new(null, token.Id, ProvisioningTokenCheck.ApplicationInactive);
        var scopes = (JsonSerializer.Deserialize<string[]>(token.ScopesJson) ?? []).ToHashSet(StringComparer.Ordinal);
        if (!scopes.Contains(requiredScope))
            return new(null, token.Id, ProvisioningTokenCheck.InsufficientScope);
        token.LastUsedAt = now; await _db.SaveChangesAsync(ct);
        return new(new ProvisioningPrincipal(token.Id, token.ApplicationSystemId, scopes), token.Id, null);
    }

    public async Task RecordRequestAsync(ScimRequestRecord request, CancellationToken ct = default)
    {
        // Its own context: the request's context may hold changes of a SCIM operation that failed.
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.ScimRequestLogs.Add(new ScimRequestLog
        {
            Id = Guid.NewGuid(), ProvisioningTokenId = request.TokenId, Method = Bound(request.Method, 10)!, Path = Bound(request.Path, 300)!,
            StatusCode = request.StatusCode, ScimType = Bound(request.ScimType, 40), Detail = Bound(request.Detail, 500),
            DurationMs = request.DurationMs, TraceId = Bound(request.TraceId, 64), CreatedAt = _clock.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<ScimDiagnosticsDto?> GetDiagnosticsAsync(Guid tokenId, CancellationToken ct = default)
    {
        var token = await _db.ProvisioningTokens.AsNoTracking().Where(item => item.Id == tokenId).Select(item => new { item.LastUsedAt }).SingleOrDefaultAsync(ct);
        if (token is null) return null;
        var now = _clock.UtcNow; var dayAgo = now.AddDays(-1); var weekAgo = now.AddDays(-7);
        var requests = _db.ScimRequestLogs.AsNoTracking().Where(item => item.ProvisioningTokenId == tokenId);
        var week = requests.Where(item => item.CreatedAt >= weekAgo);
        var failures = await week.Where(item => item.StatusCode >= 400)
            .GroupBy(item => new { item.StatusCode, item.ScimType })
            .Select(group => new { group.Key.StatusCode, group.Key.ScimType, Count = group.Count(), LastAt = group.Max(item => item.CreatedAt) })
            .OrderByDescending(group => group.Count).ThenByDescending(group => group.LastAt).Take(RecentFailureKinds)
            .ToListAsync(ct);
        var summaries = new List<ScimFailureSummaryDto>();
        foreach (var failure in failures)
        {
            var lastDetail = await week.Where(item => item.StatusCode == failure.StatusCode && item.ScimType == failure.ScimType)
                .OrderByDescending(item => item.CreatedAt).Select(item => item.Detail).FirstOrDefaultAsync(ct);
            summaries.Add(new ScimFailureSummaryDto { StatusCode = failure.StatusCode, ScimType = failure.ScimType, Count = failure.Count, LastAt = failure.LastAt, LastDetail = lastDetail });
        }
        return new ScimDiagnosticsDto
        {
            TokenId = tokenId,
            LastUsedAt = token.LastUsedAt,
            LastSucceededAt = await requests.Where(item => item.StatusCode < 400).MaxAsync(item => (DateTime?)item.CreatedAt, ct),
            LastFailedAt = await requests.Where(item => item.StatusCode >= 400).MaxAsync(item => (DateTime?)item.CreatedAt, ct),
            Last24Hours = new ScimRequestCountsDto
            {
                Total = await requests.CountAsync(item => item.CreatedAt >= dayAgo, ct),
                Failed = await requests.CountAsync(item => item.CreatedAt >= dayAgo && item.StatusCode >= 400, ct)
            },
            Last7Days = new ScimRequestCountsDto
            {
                Total = await week.CountAsync(ct),
                Failed = await week.CountAsync(item => item.StatusCode >= 400, ct)
            },
            Failures = summaries
        };
    }

    public async Task<PagedResult<ScimRequestLogDto>?> GetRequestsAsync(Guid tokenId, ScimRequestLogQuery query, CancellationToken ct = default)
    {
        if (!await _db.ProvisioningTokens.AnyAsync(item => item.Id == tokenId, ct)) return null;
        var requests = _db.ScimRequestLogs.AsNoTracking().Where(item => item.ProvisioningTokenId == tokenId);
        requests = query.Outcome?.Trim().ToLowerInvariant() switch
        {
            "failed" => requests.Where(item => item.StatusCode >= 400),
            "succeeded" => requests.Where(item => item.StatusCode < 400),
            _ => requests
        };
        var total = await requests.CountAsync(ct);
        var items = await requests.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Skip(query.Skip).Take(query.PageSize)
            .Select(item => new ScimRequestLogDto
            {
                Id = item.Id, CreatedAt = item.CreatedAt, Method = item.Method, Path = item.Path, StatusCode = item.StatusCode,
                ScimType = item.ScimType, Detail = item.Detail, DurationMs = item.DurationMs, TraceId = item.TraceId
            })
            .ToListAsync(ct);
        return PagedResult<ScimRequestLogDto>.Create(items, total, query.Page, query.PageSize);
    }

    private static string? Bound(string? value, int length) => value is null || value.Length <= length ? value : value[..length];

    private async Task<ProvisioningTokenResponse> CreateStoredAsync(Guid applicationId, string name, IReadOnlyList<string> scopes, DateTime expiresAt, CancellationToken ct)
    {
        var raw = $"acp_{WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))}";
        var entity = new ProvisioningToken { Id = Guid.NewGuid(), ApplicationSystemId = applicationId, Name = name, TokenHash = Hash(raw), ScopesJson = JsonSerializer.Serialize(scopes), CreatedAt = _clock.UtcNow, ExpiresAt = expiresAt };
        _db.ProvisioningTokens.Add(entity); await _db.SaveChangesAsync(ct); var applicationCode = await _db.ApplicationSystems.Where(x => x.Id == applicationId).Select(x => x.Code).SingleAsync(ct); await _audit.LogAsync("PROVISIONING_TOKEN_CREATED", applicationCode: applicationCode, entityName: nameof(ProvisioningToken), entityId: entity.Id.ToString(), metadata: new { result = "Success", scopes, expiresAt }, ct: ct);
        return new ProvisioningTokenResponse { Id = entity.Id, Token = raw, Scopes = scopes, ExpiresAt = expiresAt };
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ProvisioningTokenMetadataDto Map(ProvisioningToken item, DateTime now) => new()
    {
        Id = item.Id, ApplicationSystemId = item.ApplicationSystemId, ApplicationName = item.ApplicationSystem.Name,
        Name = item.Name, Scopes = JsonSerializer.Deserialize<string[]>(item.ScopesJson) ?? [],
        Status = item.RevokedAt.HasValue ? "revoked" : item.ExpiresAt <= now ? "expired" : "active",
        CreatedAt = item.CreatedAt, ExpiresAt = item.ExpiresAt, LastUsedAt = item.LastUsedAt, RevokedAt = item.RevokedAt
    };
}
