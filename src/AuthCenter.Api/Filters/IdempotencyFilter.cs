using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Responses;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;

namespace AuthCenter.Api.Filters;

/// <summary>
/// A mutation a client may retry safely: sent with an <c>Idempotency-Key</c> header, a repeated
/// request returns the first response instead of acting twice.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotentAttribute() : TypeFilterAttribute(typeof(IdempotencyFilter));

/// <summary>
/// Keys are scoped to the caller, the method and the path, and remembered for 24 hours. The same
/// key with the same request replays the stored response (<c>Idempotent-Replayed: true</c>); with a
/// different request it is refused (422); while the first request runs, a duplicate gets 409. A
/// server error or an exception releases the key so the request can be retried. Stored responses
/// are encrypted, since some carry a secret shown once (client secrets, provisioning tokens).
/// </summary>
public sealed class IdempotencyFilter(
    ITransientStateStore store,
    IDataProtectionProvider dataProtection,
    IOptions<JsonOptions> jsonOptions,
    IDateTimeProvider clock) : IAsyncActionFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";
    private const string ClaimPurpose = "idempotency_claim";
    private const string ResponsePurpose = "idempotency_response";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[HeaderName].ToString();
        var caller = http.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(key) || caller is null)
        {
            await next();
            return;
        }
        if (key.Length > 100 || key.Any(character => character is < '!' or > '~'))
        {
            context.Result = Failure(StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_INVALID", "Idempotency-Key must be 1-100 visible ASCII characters.");
            return;
        }

        var ct = http.RequestAborted;
        var scope = Hash($"{caller}\n{http.Request.Method}\n{http.Request.Path}\n{key}");
        var fingerprint = Hash(JsonSerializer.Serialize(BoundArguments(context), jsonOptions.Value.JsonSerializerOptions));
        var protector = dataProtection.CreateProtector("AuthCenter.Idempotency.v1");
        var expiresAt = clock.UtcNow.Add(Lifetime);

        if (!await store.TryConsumeAsync(ClaimPurpose, scope, expiresAt, ct))
        {
            var stored = await store.GetAsync(ResponsePurpose, scope, ct);
            if (stored is null)
            {
                context.Result = Failure(StatusCodes.Status409Conflict, "IDEMPOTENCY_KEY_IN_PROGRESS", "A request with this idempotency key is still being processed.");
                return;
            }
            var response = JsonSerializer.Deserialize<StoredResponse>(protector.Unprotect(stored))!;
            if (!string.Equals(response.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                context.Result = Failure(StatusCodes.Status422UnprocessableEntity, "IDEMPOTENCY_KEY_REUSED", "This idempotency key was already used for a different request.");
                return;
            }
            http.Response.Headers[ReplayedHeaderName] = "true";
            context.Result = new ContentResult { StatusCode = response.Status, Content = response.Body, ContentType = response.Body is null ? null : "application/json; charset=utf-8" };
            return;
        }

        var executed = await next();
        var outcome = Describe(executed.Result);
        if (executed.Exception is not null && !executed.ExceptionHandled || outcome is null || outcome.Value.Status >= 500)
        {
            // Nothing (reliable) happened: let the client retry with the same key.
            await store.RemoveAsync(ClaimPurpose, scope, CancellationToken.None);
            return;
        }
        var record = new StoredResponse(fingerprint, outcome.Value.Status, outcome.Value.Body);
        await store.SetAsync(ResponsePurpose, scope, protector.Protect(JsonSerializer.Serialize(record)), expiresAt, CancellationToken.None);
    }

    /// <summary>The request as the action saw it: route, query and body values, not services.</summary>
    private static SortedDictionary<string, object?> BoundArguments(ActionExecutingContext context)
    {
        var arguments = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var parameter in context.ActionDescriptor.Parameters)
        {
            var source = parameter.BindingInfo?.BindingSource;
            if (source == BindingSource.Services || source == BindingSource.Special || parameter.ParameterType == typeof(CancellationToken))
                continue;
            arguments[parameter.Name] = context.ActionArguments.TryGetValue(parameter.Name, out var value) ? value : null;
        }
        return arguments;
    }

    private (int Status, string? Body)? Describe(IActionResult? result) => result switch
    {
        ObjectResult objectResult => (ConcurrencyConflictResultFilter.EffectiveStatus(objectResult),
            JsonSerializer.Serialize(objectResult.Value, objectResult.Value?.GetType() ?? typeof(object), jsonOptions.Value.JsonSerializerOptions)),
        StatusCodeResult statusResult => (statusResult.StatusCode, null),
        _ => null
    };

    private static ObjectResult Failure(int status, string code, string message) =>
        new(ApiResponse.Fail(code, message)) { StatusCode = status };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record StoredResponse(string Fingerprint, int Status, string? Body);
}
