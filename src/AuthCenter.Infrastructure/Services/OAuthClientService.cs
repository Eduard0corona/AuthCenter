using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.OAuth;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public class OAuthClientService : IOAuthClientService
{
    private readonly AuthCenterDbContext _db;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ICurrentUserService _currentUser;

    public OAuthClientService(
        AuthCenterDbContext db,
        IDateTimeProvider dateTimeProvider,
        ICurrentUserService currentUser)
    {
        _db = db;
        _dateTimeProvider = dateTimeProvider;
        _currentUser = currentUser;
    }

    public async Task<OperationResult<OAuthClientCreatedResponse>> CreateAsync(CreateOAuthClientRequest request, CancellationToken ct = default)
    {
        var application = await _db.ApplicationSystems
            .FirstOrDefaultAsync(a => a.Id == request.ApplicationSystemId, ct);
        if (application is null)
            return OperationResult<OAuthClientCreatedResponse>.Failure("APPLICATION_NOT_FOUND", "Application was not found.");
        if (!application.IsActive)
            return OperationResult<OAuthClientCreatedResponse>.Failure("APPLICATION_INACTIVE", "OAuth clients cannot be created for an inactive application.");

        var exists = await _db.OAuthClients.AnyAsync(c => c.ClientId == request.ClientId, ct);
        if (exists)
            return OperationResult<OAuthClientCreatedResponse>.Failure("CLIENT_ID_TAKEN", "A client with this ClientId already exists.");

        string? plainSecret = null;
        string? hashedSecret = null;

        if ((OAuthClientType)request.ClientType == OAuthClientType.Confidential)
        {
            var secretBytes = new byte[32];
            RandomNumberGenerator.Fill(secretBytes);
            plainSecret = Convert.ToBase64String(secretBytes);
            hashedSecret = HashSecret(plainSecret);
        }

        var client = new OAuthClient
        {
            Id = Guid.NewGuid(),
            ApplicationSystemId = application.Id,
            ApplicationSystem = application,
            ClientId = request.ClientId,
            HashedClientSecret = hashedSecret,
            DisplayName = request.DisplayName,
            RedirectUrisJson = JsonSerializer.Serialize(request.RedirectUris),
            AllowedScopesJson = JsonSerializer.Serialize(request.AllowedScopes),
            GrantTypesJson = JsonSerializer.Serialize(request.GrantTypes),
            ClientType = (OAuthClientType)request.ClientType,
            LoginUrl = request.LoginUrl,
            PostLogoutRedirectUrisJson = JsonSerializer.Serialize(request.PostLogoutRedirectUris),
            BackchannelLogoutUri = string.IsNullOrWhiteSpace(request.BackchannelLogoutUri) ? null : request.BackchannelLogoutUri,
            BackchannelLogoutSessionRequired = request.BackchannelLogoutSessionRequired,
            AccessTokenLifetimeSeconds = request.AccessTokenLifetimeSeconds,
            RequirePkce = request.RequirePkce,
            AutoConsent = request.AutoConsent,
            IsActive = true,
            CreatedAt = _dateTimeProvider.UtcNow
        };

        _db.OAuthClients.Add(client);
        AddAudit("OAUTH_CLIENT_CREATED", client);
        await _db.SaveChangesAsync(ct);

        return OperationResult<OAuthClientCreatedResponse>.Success(new OAuthClientCreatedResponse
        {
            Client = MapToResponse(client),
            ClientSecret = plainSecret
        });
    }

    public async Task<PagedResult<OAuthClientResponse>> GetAllAsync(OAuthClientQuery request, CancellationToken ct = default)
    {
        var query = _db.OAuthClients
            .AsNoTracking()
            .Include(c => c.ApplicationSystem)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(client =>
                client.DisplayName.Contains(search) ||
                client.ClientId.Contains(search));
        }

        if (request.ApplicationSystemId.HasValue)
            query = query.Where(client => client.ApplicationSystemId == request.ApplicationSystemId.Value);
        if (request.ClientType.HasValue)
            query = query.Where(client => (int)client.ClientType == request.ClientType.Value);
        if (request.IsActive.HasValue)
            query = query.Where(client => client.IsActive == request.IsActive.Value);

        var totalCount = await query.CountAsync(ct);
        var clients = await query
            .OrderBy(client => client.DisplayName)
            .ThenBy(client => client.ClientId)
            .Skip(request.Skip)
            .Take(request.PageSize)
            .ToListAsync(ct);
        return PagedResult<OAuthClientResponse>.Create(
            clients.Select(MapToResponse).ToList(),
            totalCount,
            request.Page,
            request.PageSize);
    }

    public async Task<OAuthClientResponse?> GetByClientIdAsync(string clientId, CancellationToken ct = default)
    {
        var client = await _db.OAuthClients
            .Include(c => c.ApplicationSystem)
            .FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        return client is null ? null : MapToResponse(client);
    }

    public async Task<OperationResult<OAuthClientResponse>> UpdateAsync(string clientId, UpdateOAuthClientRequest request, CancellationToken ct = default)
    {
        var client = await _db.OAuthClients
            .Include(c => c.ApplicationSystem)
            .FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (client is null)
            return OperationResult<OAuthClientResponse>.Failure("NOT_FOUND", "OAuth client not found.");
        if (client.ClientType == OAuthClientType.Public && request.GrantTypes.Contains("client_credentials"))
            return OperationResult<OAuthClientResponse>.Failure("INVALID_GRANT_CONFIGURATION", "Public clients cannot use client_credentials.");

        var statusChanged = client.IsActive != request.IsActive;
        client.DisplayName = request.DisplayName;
        client.RedirectUrisJson = JsonSerializer.Serialize(request.RedirectUris);
        client.AllowedScopesJson = JsonSerializer.Serialize(request.AllowedScopes);
        client.GrantTypesJson = JsonSerializer.Serialize(request.GrantTypes);
        client.LoginUrl = request.LoginUrl;
        client.PostLogoutRedirectUrisJson = JsonSerializer.Serialize(request.PostLogoutRedirectUris);
        client.BackchannelLogoutUri = string.IsNullOrWhiteSpace(request.BackchannelLogoutUri) ? null : request.BackchannelLogoutUri;
        client.BackchannelLogoutSessionRequired = request.BackchannelLogoutSessionRequired;
        client.AccessTokenLifetimeSeconds = request.AccessTokenLifetimeSeconds;
        client.RequirePkce = request.RequirePkce;
        client.AutoConsent = request.AutoConsent;
        client.IsActive = request.IsActive;
        client.UpdatedAt = _dateTimeProvider.UtcNow;

        AddAudit(statusChanged
            ? request.IsActive ? "OAUTH_CLIENT_ACTIVATED" : "OAUTH_CLIENT_DEACTIVATED"
            : "OAUTH_CLIENT_UPDATED", client);
        await _db.SaveChangesAsync(ct);
        return OperationResult<OAuthClientResponse>.Success(MapToResponse(client));
    }

    public async Task<OperationResult> DeactivateAsync(string clientId, CancellationToken ct = default)
    {
        var client = await _db.OAuthClients
            .Include(c => c.ApplicationSystem)
            .FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (client is null)
            return OperationResult.Failure("NOT_FOUND", "OAuth client not found.");

        client.IsActive = false;
        client.UpdatedAt = _dateTimeProvider.UtcNow;
        AddAudit("OAUTH_CLIENT_DEACTIVATED", client);
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult<RotateClientSecretResponse>> RotateSecretAsync(string clientId, CancellationToken ct = default)
    {
        var client = await _db.OAuthClients
            .Include(c => c.ApplicationSystem)
            .FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (client is null)
            return OperationResult<RotateClientSecretResponse>.Failure("NOT_FOUND", "OAuth client not found.");
        if (client.ClientType != OAuthClientType.Confidential)
            return OperationResult<RotateClientSecretResponse>.Failure("INVALID_CLIENT_TYPE", "Only Confidential clients have a client secret.");

        var secretBytes = new byte[32];
        RandomNumberGenerator.Fill(secretBytes);
        var plainSecret = Convert.ToBase64String(secretBytes);
        client.HashedClientSecret = HashSecret(plainSecret);
        client.UpdatedAt = _dateTimeProvider.UtcNow;
        AddAudit("OAUTH_CLIENT_SECRET_ROTATED", client);
        await _db.SaveChangesAsync(ct);

        return OperationResult<RotateClientSecretResponse>.Success(new RotateClientSecretResponse { ClientSecret = plainSecret });
    }

    internal static OAuthClientResponse MapToResponse(OAuthClient client) => new()
    {
        Id = client.Id,
        ApplicationSystemId = client.ApplicationSystemId,
        ApplicationCode = client.ApplicationSystem.Code,
        ApplicationName = client.ApplicationSystem.Name,
        ClientId = client.ClientId,
        DisplayName = client.DisplayName,
        ClientType = (int)client.ClientType,
        RedirectUris = JsonSerializer.Deserialize<List<string>>(client.RedirectUrisJson) ?? [],
        AllowedScopes = JsonSerializer.Deserialize<List<string>>(client.AllowedScopesJson) ?? [],
        GrantTypes = JsonSerializer.Deserialize<List<string>>(client.GrantTypesJson) ?? [],
        LoginUrl = client.LoginUrl,
        PostLogoutRedirectUris = JsonSerializer.Deserialize<List<string>>(client.PostLogoutRedirectUrisJson) ?? [],
        BackchannelLogoutUri = client.BackchannelLogoutUri,
        BackchannelLogoutSessionRequired = client.BackchannelLogoutSessionRequired,
        AccessTokenLifetimeSeconds = client.AccessTokenLifetimeSeconds,
        RequirePkce = client.RequirePkce,
        AutoConsent = client.AutoConsent,
        IsActive = client.IsActive,
        CreatedAt = client.CreatedAt,
        UpdatedAt = client.UpdatedAt
    };

    private static string HashSecret(string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(bytes);
    }

    private void AddAudit(string action, OAuthClient client)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = _currentUser.UserId,
            ApplicationCode = client.ApplicationSystem.Code,
            Action = action,
            EntityName = nameof(OAuthClient),
            EntityId = client.ClientId,
            CreatedAt = _dateTimeProvider.UtcNow
        });
    }
}
