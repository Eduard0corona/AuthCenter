using System.Security.Cryptography;
using System.Text.Json;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses.Lifecycle;
using AuthCenter.Domain.Entities;
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
    public async Task<OperationResult<EventHookSecretResponse>> CreateAsync(CreateEventHookRequest request, CancellationToken ct = default)
    {
        var types = request.EventTypes.Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 150).Distinct(StringComparer.Ordinal).ToArray(); if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 150 || types.Length is 0 or > 100 || !await OutboundUrlSafety.IsPublicHttpsAsync(request.Url, ct)) return OperationResult<EventHookSecretResponse>.Failure("INVALID_EVENT_HOOK", "Name, public HTTPS URL, and 1-100 event types are required."); if (request.ApplicationSystemId.HasValue && !await _db.ApplicationSystems.AnyAsync(x => x.Id == request.ApplicationSystemId && x.IsActive, ct)) return OperationResult<EventHookSecretResponse>.Failure("APP_NOT_FOUND", "Active application not found.");
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)); var hook = new EventHook { Id = Guid.NewGuid(), ApplicationSystemId = request.ApplicationSystemId, Name = request.Name.Trim(), Url = request.Url.Trim(), ProtectedSecret = _protector.Protect(secret), EventTypesJson = JsonSerializer.Serialize(types), IsVerified = false, IsActive = true, CreatedAt = _clock.UtcNow }; _db.EventHooks.Add(hook); try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { return OperationResult<EventHookSecretResponse>.Failure("EVENT_HOOK_EXISTS", "An event hook with this name already exists."); }
        await _audit.LogAsync("EVENT_HOOK_CREATED", entityName: nameof(EventHook), entityId: hook.Id.ToString(), ct: ct); return OperationResult<EventHookSecretResponse>.Success(new EventHookSecretResponse { Id = hook.Id, Secret = secret, IsVerified = false });
    }
    public async Task<OperationResult> VerifyAsync(Guid id, CancellationToken ct = default)
    {
        var hook = await _db.EventHooks.FindAsync([id], ct); if (hook is null || !hook.IsActive) return OperationResult.Failure("EVENT_HOOK_NOT_FOUND", "Active event hook not found."); if (!await OutboundUrlSafety.IsPublicHttpsAsync(hook.Url, ct)) return OperationResult.Failure("EVENT_HOOK_UNSAFE_URL", "Event hook URL no longer resolves to a public endpoint."); var challenge = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24)); using var request = new HttpRequestMessage(HttpMethod.Get, Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(hook.Url, "verification_challenge", challenge)); request.Headers.Add("X-AuthCenter-Verification", challenge); using var response = await _clients.CreateClient("EventHooks").SendAsync(request, ct); var body = await response.Content.ReadAsStringAsync(ct); var verified = response.IsSuccessStatusCode && (response.Headers.TryGetValues("X-AuthCenter-Verification", out var values) && values.Contains(challenge, StringComparer.Ordinal) || BodyContains(body, challenge)); if (!verified) return OperationResult.Failure("EVENT_HOOK_VERIFICATION_FAILED", "Endpoint did not echo the verification challenge."); hook.IsVerified = true; hook.VerifiedAt = _clock.UtcNow; await _db.SaveChangesAsync(ct); await _audit.LogAsync("EVENT_HOOK_VERIFIED", entityName: nameof(EventHook), entityId: id.ToString(), ct: ct); return OperationResult.Success();
    }
    public async Task<OperationResult> DeleteAsync(Guid id, CancellationToken ct = default) { var hook = await _db.EventHooks.FindAsync([id], ct); if (hook is null) return OperationResult.Failure("EVENT_HOOK_NOT_FOUND", "Event hook not found."); hook.IsActive = false; await _db.SaveChangesAsync(ct); await _audit.LogAsync("EVENT_HOOK_DISABLED", entityName: nameof(EventHook), entityId: id.ToString(), ct: ct); return OperationResult.Success(); }
    public async Task<OperationResult> ReplayDeadLetterAsync(Guid deliveryId, CancellationToken ct = default) { var d = await _db.EventHookDeliveries.FindAsync([deliveryId], ct); if (d?.DeadLetteredAt is null) return OperationResult.Failure("DEAD_LETTER_NOT_FOUND", "Dead-lettered delivery not found."); d.DeadLetteredAt = null; d.AttemptCount = 0; d.LastError = null; d.NextAttemptAt = _clock.UtcNow; await _db.SaveChangesAsync(ct); return OperationResult.Success(); }
    public async Task<IReadOnlyList<object>> GetDeliveriesAsync(bool deadLettersOnly, CancellationToken ct = default) { var query = _db.EventHookDeliveries.AsNoTracking().Include(x => x.EventHook).AsQueryable(); if (deadLettersOnly) query = query.Where(x => x.DeadLetteredAt != null); return await query.OrderByDescending(x => x.NextAttemptAt).Take(200).Select(x => (object)new { x.Id, x.EventId, x.EventType, hookId = x.EventHookId, hookName = x.EventHook.Name, x.AttemptCount, x.NextAttemptAt, x.DeliveredAt, x.DeadLetteredAt, x.LastError }).ToListAsync(ct); }
    private static bool BodyContains(string body, string challenge) { try { using var doc = JsonDocument.Parse(body); return doc.RootElement.TryGetProperty("verification", out var v) && v.GetString() == challenge; } catch (JsonException) { return false; } }
}
