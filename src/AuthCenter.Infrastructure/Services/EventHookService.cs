using System.Security.Cryptography;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Lifecycle;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Events;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public sealed class EventHookService : IEventHookService
{
    private readonly AuthCenterDbContext _db; private readonly IDateTimeProvider _clock; private readonly IAuditService _audit; private readonly IDataProtector _protector; private readonly IHttpClientFactory _clients;
    public EventHookService(AuthCenterDbContext db, IDateTimeProvider clock, IAuditService audit, IDataProtectionProvider protection, IHttpClientFactory clients) { _db = db; _clock = clock; _audit = audit; _protector = protection.CreateProtector("AuthCenter.EventHookSecrets.v1"); _clients = clients; }

    public async Task<PagedResult<EventHookDto>> GetAsync(EventHookQuery query, CancellationToken ct = default)
    {
        var hooks = HookQuery().AsNoTracking();
        if (query.ApplicationSystemId.HasValue) hooks = hooks.Where(x => x.ApplicationSystemId == query.ApplicationSystemId);
        if (query.IsActive.HasValue) hooks = hooks.Where(x => x.IsActive == query.IsActive);
        if (query.IsVerified.HasValue) hooks = hooks.Where(x => x.IsVerified == query.IsVerified);
        if (!string.IsNullOrWhiteSpace(query.Search)) { var search = query.Search.Trim(); hooks = hooks.Where(x => x.Name.Contains(search) || x.Url.Contains(search)); }
        var total = await hooks.CountAsync(ct);
        var items = await hooks.OrderBy(x => x.Name).Skip(query.Skip).Take(query.PageSize).ToListAsync(ct);
        return PagedResult<EventHookDto>.Create(items.Select(Map).ToList(), total, query.Page, query.PageSize);
    }

    public async Task<EventHookDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var hook = await HookQuery().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return hook is null ? null : Map(hook);
    }

    public async Task<OperationResult<EventHookSecretResponse>> CreateAsync(CreateEventHookRequest request, CancellationToken ct = default)
    {
        var validation = await ValidateAsync(request.ApplicationSystemId, request.Name, request.Url, request.EventTypes, ct);
        if (validation is not null) return OperationResult<EventHookSecretResponse>.Failure(validation.Value.Code, validation.Value.Message);
        var types = NormalizeTypes(request.EventTypes); var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var hook = new EventHook { Id = Guid.NewGuid(), ApplicationSystemId = request.ApplicationSystemId, Name = request.Name.Trim(), Url = request.Url.Trim(), ProtectedSecret = _protector.Protect(secret), EventTypesJson = JsonSerializer.Serialize(types), IsVerified = false, IsActive = true, CreatedAt = _clock.UtcNow };
        _db.EventHooks.Add(hook); try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { return OperationResult<EventHookSecretResponse>.Failure("EVENT_HOOK_EXISTS", "An event hook with this name already exists."); }
        await _audit.LogAsync("EVENT_HOOK_CREATED", applicationCode: await ApplicationCodeAsync(hook.ApplicationSystemId, ct), entityName: nameof(EventHook), entityId: hook.Id.ToString(), metadata: new { result = "Success", eventTypes = types, hasSecret = true }, ct: ct);
        return OperationResult<EventHookSecretResponse>.Success(new EventHookSecretResponse { Id = hook.Id, Secret = secret, IsVerified = false });
    }

    public async Task<OperationResult<EventHookDto>> UpdateAsync(Guid id, UpdateEventHookRequest request, CancellationToken ct = default)
    {
        var hook = await _db.EventHooks.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (hook is null) return OperationResult<EventHookDto>.Failure("EVENT_HOOK_NOT_FOUND", "Event hook not found.");
        if (hook.Version != request.Version) return OperationResult<EventHookDto>.Failure("CONCURRENCY_CONFLICT", "The event hook changed after it was loaded.");
        var validation = await ValidateAsync(hook.ApplicationSystemId, request.Name, request.Url, request.EventTypes, ct);
        if (validation is not null) return OperationResult<EventHookDto>.Failure(validation.Value.Code, validation.Value.Message);
        var urlChanged = !string.Equals(hook.Url, request.Url.Trim(), StringComparison.Ordinal);
        hook.Name = request.Name.Trim(); hook.Url = request.Url.Trim(); hook.EventTypesJson = JsonSerializer.Serialize(NormalizeTypes(request.EventTypes)); hook.IsActive = request.IsActive; hook.Version++;
        if (urlChanged) { hook.IsVerified = false; hook.VerifiedAt = null; }
        try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { return OperationResult<EventHookDto>.Failure("EVENT_HOOK_EXISTS", "An event hook with this name already exists."); }
        await _audit.LogAsync("EVENT_HOOK_UPDATED", applicationCode: await ApplicationCodeAsync(hook.ApplicationSystemId, ct), entityName: nameof(EventHook), entityId: id.ToString(), metadata: new { result = "Success", hook.IsActive, hook.IsVerified, hook.Version }, ct: ct);
        return OperationResult<EventHookDto>.Success(Map((await HookQuery().AsNoTracking().SingleAsync(x => x.Id == id, ct))));
    }

    public async Task<OperationResult> VerifyAsync(Guid id, CancellationToken ct = default)
    {
        var hook = await _db.EventHooks.FindAsync([id], ct); if (hook is null || !hook.IsActive) return OperationResult.Failure("EVENT_HOOK_NOT_FOUND", "Active event hook not found.");
        if (!await OutboundUrlSafety.IsPublicHttpsAsync(hook.Url, ct)) return OperationResult.Failure("EVENT_HOOK_UNSAFE_URL", "Event hook URL no longer resolves to a public endpoint.");
        var challenge = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24)); using var request = new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString(hook.Url, "verification_challenge", challenge)); request.Headers.Add("X-AuthCenter-Verification", challenge);
        using var response = await _clients.CreateClient("EventHooks").SendAsync(request, ct); var body = await response.Content.ReadAsStringAsync(ct); var verified = response.IsSuccessStatusCode && (response.Headers.TryGetValues("X-AuthCenter-Verification", out var values) && values.Contains(challenge, StringComparer.Ordinal) || BodyContains(body, challenge));
        if (!verified) { await _audit.LogAsync("EVENT_HOOK_VERIFICATION_REJECTED", applicationCode: await ApplicationCodeAsync(hook.ApplicationSystemId, ct), entityName: nameof(EventHook), entityId: id.ToString(), metadata: new { result = "Rejected", reason = "ChallengeMismatch" }, ct: ct); return OperationResult.Failure("EVENT_HOOK_VERIFICATION_FAILED", "Endpoint did not echo the verification challenge."); }
        hook.IsVerified = true; hook.VerifiedAt = _clock.UtcNow; hook.Version++; await _db.SaveChangesAsync(ct); await _audit.LogAsync("EVENT_HOOK_VERIFIED", applicationCode: await ApplicationCodeAsync(hook.ApplicationSystemId, ct), entityName: nameof(EventHook), entityId: id.ToString(), metadata: new { result = "Success" }, ct: ct); return OperationResult.Success();
    }

    public async Task<OperationResult> DeleteAsync(Guid id, CancellationToken ct = default) { var hook = await _db.EventHooks.FindAsync([id], ct); if (hook is null) return OperationResult.Failure("EVENT_HOOK_NOT_FOUND", "Event hook not found."); hook.IsActive = false; hook.Version++; await _db.SaveChangesAsync(ct); await _audit.LogAsync("EVENT_HOOK_DISABLED", applicationCode: await ApplicationCodeAsync(hook.ApplicationSystemId, ct), entityName: nameof(EventHook), entityId: id.ToString(), metadata: new { result = "Success" }, ct: ct); return OperationResult.Success(); }

    public async Task<OperationResult> ReplayDeadLetterAsync(Guid deliveryId, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100) return OperationResult.Failure("IDEMPOTENCY_KEY_REQUIRED", "A valid Idempotency-Key header is required.");
        var d = await _db.EventHookDeliveries.Include(x => x.EventHook).SingleOrDefaultAsync(x => x.Id == deliveryId, ct);
        if (d is null) return OperationResult.Failure("DEAD_LETTER_NOT_FOUND", "Dead-lettered delivery not found.");
        if (string.Equals(d.LastReplayIdempotencyKey, idempotencyKey, StringComparison.Ordinal)) return OperationResult.Success();
        if (d.DeadLetteredAt is null) return OperationResult.Failure("DEAD_LETTER_NOT_FOUND", "Dead-lettered delivery not found.");
        d.DeadLetteredAt = null; d.AttemptCount = 0; d.LastError = null; d.NextAttemptAt = _clock.UtcNow; d.LastReplayIdempotencyKey = idempotencyKey; await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("EVENT_HOOK_DELIVERY_REPLAYED", applicationCode: await ApplicationCodeAsync(d.EventHook.ApplicationSystemId, ct), entityName: nameof(EventHookDelivery), entityId: deliveryId.ToString(), metadata: new { result = "Success", d.EventHookId, d.EventId }, ct: ct); return OperationResult.Success();
    }

    /// <summary>How long deliveries keep a signature made with the rotated-out secret.</summary>
    private static readonly TimeSpan SecretGracePeriod = TimeSpan.FromHours(24);

    public async Task<OperationResult<EventHookSecretResponse>> RotateSecretAsync(Guid id, CancellationToken ct = default)
    {
        var hook = await _db.EventHooks.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (hook is null) return OperationResult<EventHookSecretResponse>.Failure("EVENT_HOOK_NOT_FOUND", "Event hook not found.");
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        // Receivers switch to the new secret while deliveries still carry a signature with the old one.
        hook.PreviousProtectedSecret = hook.ProtectedSecret;
        hook.PreviousSecretExpiresAt = _clock.UtcNow.Add(SecretGracePeriod);
        hook.ProtectedSecret = _protector.Protect(secret);
        hook.Version++;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("EVENT_HOOK_SECRET_ROTATED", applicationCode: await ApplicationCodeAsync(hook.ApplicationSystemId, ct), entityName: nameof(EventHook), entityId: id.ToString(), metadata: new { result = "Success", previousSecretExpiresAt = hook.PreviousSecretExpiresAt }, ct: ct);
        return OperationResult<EventHookSecretResponse>.Success(new EventHookSecretResponse { Id = hook.Id, Secret = secret, IsVerified = hook.IsVerified, PreviousSecretExpiresAt = hook.PreviousSecretExpiresAt });
    }

    public async Task<EventHookDeliveryDto?> GetDeliveryAsync(Guid id, CancellationToken ct = default)
    {
        var delivery = await _db.EventHookDeliveries.AsNoTracking().Include(x => x.EventHook).SingleOrDefaultAsync(x => x.Id == id, ct);
        return delivery is null ? null : Map(delivery, includePayload: true);
    }

    public IReadOnlyList<EventTypeDto> GetEventTypes() =>
        EventTypes.All.Select(item => new EventTypeDto { Type = item.Type, Category = item.Category }).ToList();

    public async Task<PagedResult<EventHookDeliveryDto>> GetDeliveriesAsync(EventHookDeliveryQuery query, CancellationToken ct = default)
    {
        var deliveries = _db.EventHookDeliveries.AsNoTracking().Include(x => x.EventHook).AsQueryable();
        if (query.HookId.HasValue) deliveries = deliveries.Where(x => x.EventHookId == query.HookId);
        if (query.EventId.HasValue) deliveries = deliveries.Where(x => x.EventId == query.EventId);
        if (!string.IsNullOrWhiteSpace(query.EventType)) deliveries = deliveries.Where(x => x.EventType == query.EventType.Trim());
        if (query.FromUtc.HasValue) deliveries = deliveries.Where(x => x.CreatedAt >= query.FromUtc);
        if (query.ToUtc.HasValue) deliveries = deliveries.Where(x => x.CreatedAt <= query.ToUtc);
        deliveries = query.Status?.Trim().ToLowerInvariant() switch { "delivered" => deliveries.Where(x => x.DeliveredAt != null), "dead-letter" => deliveries.Where(x => x.DeadLetteredAt != null), "pending" => deliveries.Where(x => x.DeliveredAt == null && x.DeadLetteredAt == null), _ => deliveries };
        var total = await deliveries.CountAsync(ct); var items = await deliveries.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.NextAttemptAt).Skip(query.Skip).Take(query.PageSize).ToListAsync(ct);
        return PagedResult<EventHookDeliveryDto>.Create(items.Select(x => Map(x, includePayload: false)).ToList(), total, query.Page, query.PageSize);
    }

    private IQueryable<EventHook> HookQuery() => _db.EventHooks.Include(x => x.ApplicationSystem);
    private async Task<(string Code, string Message)?> ValidateAsync(Guid? applicationId, string name, string url, IReadOnlyList<string> eventTypes, CancellationToken ct)
    {
        var types = NormalizeTypes(eventTypes);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 150 || types.Length is 0 or > 100 || !await OutboundUrlSafety.IsPublicHttpsAsync(url, ct))
            return ("INVALID_EVENT_HOOK", "Name, public HTTPS URL, and 1-100 event types are required.");
        // A misspelled type would silently never fire.
        var unknown = types.Where(type => type != EventTypes.Wildcard && !EventTypes.IsKnown(type)).ToList();
        if (unknown.Count > 0)
            return ("UNKNOWN_EVENT_TYPE", $"Unknown event types: {string.Join(", ", unknown.Take(10))}. See GET /api/event-hooks/event-types.");
        if (applicationId.HasValue && !await _db.ApplicationSystems.AnyAsync(x => x.Id == applicationId && x.IsActive, ct))
            return ("APP_NOT_FOUND", "Active application not found.");
        return null;
    }
    private Task<string?> ApplicationCodeAsync(Guid? id, CancellationToken ct) => id.HasValue ? _db.ApplicationSystems.Where(x => x.Id == id).Select(x => x.Code).SingleOrDefaultAsync(ct) : Task.FromResult<string?>(null);
    private static string[] NormalizeTypes(IReadOnlyList<string> values) => values.Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 150).Distinct(StringComparer.Ordinal).ToArray();
    private EventHookDto Map(EventHook x) => new() { Id = x.Id, ApplicationSystemId = x.ApplicationSystemId, ApplicationName = x.ApplicationSystem?.Name, Name = x.Name, Url = x.Url, EventTypes = JsonSerializer.Deserialize<string[]>(x.EventTypesJson) ?? [], IsVerified = x.IsVerified, IsActive = x.IsActive, CreatedAt = x.CreatedAt, VerifiedAt = x.VerifiedAt, Version = x.Version, PreviousSecretExpiresAt = x.PreviousSecretExpiresAt > _clock.UtcNow ? x.PreviousSecretExpiresAt : null };
    private static EventHookDeliveryDto Map(EventHookDelivery x, bool includePayload) => new() { Id = x.Id, EventId = x.EventId, EventType = x.EventType, HookId = x.EventHookId, HookName = x.EventHook.Name, Status = x.DeliveredAt.HasValue ? "delivered" : x.DeadLetteredAt.HasValue ? "dead-letter" : "pending", AttemptCount = x.AttemptCount, CreatedAt = x.CreatedAt, NextAttemptAt = x.NextAttemptAt, DeliveredAt = x.DeliveredAt, DeadLetteredAt = x.DeadLetteredAt, LastError = x.LastError, Payload = includePayload ? x.PayloadJson : null };
    private static bool BodyContains(string body, string challenge) { try { using var doc = JsonDocument.Parse(body); return doc.RootElement.TryGetProperty("verification", out var v) && v.GetString() == challenge; } catch (JsonException) { return false; } }
}
