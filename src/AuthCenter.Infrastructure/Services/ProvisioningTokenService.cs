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
    private readonly AuthCenterDbContext _db; private readonly IDateTimeProvider _clock; private readonly IAuditService _audit;
    public ProvisioningTokenService(AuthCenterDbContext db, IDateTimeProvider clock, IAuditService audit) { _db = db; _clock = clock; _audit = audit; }

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

    public async Task<ProvisioningPrincipal?> ValidateAsync(string? rawToken, string requiredScope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken) || !rawToken.StartsWith("acp_", StringComparison.Ordinal) || !AllowedScopes.Contains(requiredScope)) return null;
        var hash = Hash(rawToken); var now = _clock.UtcNow;
        var token = await _db.ProvisioningTokens.SingleOrDefaultAsync(item => item.TokenHash == hash && item.RevokedAt == null && item.ExpiresAt > now && item.ApplicationSystem.IsActive, ct);
        if (token is null) return null;
        var scopes = (JsonSerializer.Deserialize<string[]>(token.ScopesJson) ?? []).ToHashSet(StringComparer.Ordinal);
        if (!scopes.Contains(requiredScope)) return null;
        token.LastUsedAt = now; await _db.SaveChangesAsync(ct);
        return new ProvisioningPrincipal(token.Id, token.ApplicationSystemId, scopes);
    }

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
