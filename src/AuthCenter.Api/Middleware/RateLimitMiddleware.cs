using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Api.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthCenter.Api.Middleware;

/// <summary>
/// Applies the endpoint's rate-limit policy (<see cref="EnableRateLimitingAttribute"/>): every rule
/// whose dimension applies to the request is counted, and any exhausted rule rejects it.
/// </summary>
public sealed class RateLimitMiddleware
{
    private const int MaxInspectedBodyBytes = 64 * 1024;
    private readonly RequestDelegate _next;
    private readonly RateLimitRules _rules;

    public RateLimitMiddleware(RequestDelegate next, RateLimitRules rules)
    {
        _next = next;
        _rules = rules;
    }

    public async Task InvokeAsync(HttpContext context, IRateLimitStore store)
    {
        var policy = _rules.Enabled ? context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName : null;
        var rules = policy is null ? [] : _rules.For(policy);
        if (rules.Count == 0)
        {
            await _next(context);
            return;
        }

        var partitions = new RequestPartitions(context);
        TimeSpan? retryAfter = null;
        foreach (var rule in rules)
        {
            var partition = await partitions.GetAsync(rule.Dimension);
            if (partition is null)
                continue;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{policy}:{rule.Dimension}:{partition}")));
            var result = await store.TryAcquireAsync(key, rule.PermitLimit, rule.Window, context.RequestAborted);
            if (!result.Acquired && (retryAfter is null || result.RetryAfter > retryAfter))
                retryAfter = result.RetryAfter;
        }

        if (retryAfter is null)
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json";
        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.Value.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.Response.WriteAsync(
            """{"success":false,"errorCode":"RATE_LIMIT_EXCEEDED","message":"Too many requests. Please try again later."}""",
            context.RequestAborted);
    }

    /// <summary>Reads each partition value at most once per request, and only when a rule needs it.</summary>
    private sealed class RequestPartitions(HttpContext context)
    {
        private string? _account;
        private bool _accountRead;
        private string? _client;
        private bool _clientRead;

        public async Task<string?> GetAsync(RateLimitDimension dimension) => dimension switch
        {
            RateLimitDimension.Ip => Address(),
            RateLimitDimension.Account => await AccountAsync(),
            RateLimitDimension.Client => await ClientAsync(),
            RateLimitDimension.AnonymousIp => await ClientAsync() is null ? Address() : null,
            _ => null
        };

        private string Address() => context.Connection.RemoteIpAddress?.ToString() ?? "anon";

        /// <summary>The email of a JSON body, normalized; the body stays readable for the endpoint.</summary>
        private async Task<string?> AccountAsync()
        {
            if (_accountRead)
                return _account;
            _accountRead = true;
            var request = context.Request;
            // Chunked bodies are read like model binding would; Kestrel's body size limit applies.
            if (request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true ||
                request.ContentLength > MaxInspectedBodyBytes)
            {
                return null;
            }

            request.EnableBuffering();
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: context.RequestAborted);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (string.Equals(property.Name, "email", StringComparison.OrdinalIgnoreCase) &&
                            property.Value.ValueKind == JsonValueKind.String &&
                            property.Value.GetString()?.Trim() is { Length: > 0 } email)
                        {
                            _account = email.ToUpperInvariant();
                            break;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed bodies are rejected by the endpoint; only the other rules apply here.
            }
            finally
            {
                request.Body.Position = 0;
            }
            return _account;
        }

        /// <summary>The client of an HTTP Basic header or a client_id form field.</summary>
        private async Task<string?> ClientAsync()
        {
            if (_clientRead)
                return _client;
            _clientRead = true;
            var authorization = context.Request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim()));
                    var separator = decoded.IndexOf(':');
                    if (separator > 0)
                        _client = Uri.UnescapeDataString(decoded[..separator]);
                }
                catch (FormatException)
                {
                    // An invalid header is rejected by the endpoint.
                }
            }

            if (_client is null && context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                _client = form["client_id"].FirstOrDefault() is { Length: > 0 and <= 200 } clientId ? clientId : null;
            }
            return _client;
        }
    }
}
