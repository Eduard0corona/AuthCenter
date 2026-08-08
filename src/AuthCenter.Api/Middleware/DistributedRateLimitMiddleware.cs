using System.Security.Cryptography;
using System.Text;
using AuthCenter.Api.Extensions;
using AuthCenter.Api.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthCenter.Api.Middleware;

public sealed class DistributedRateLimitMiddleware
{
    private readonly RequestDelegate _next;

    public DistributedRateLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, DistributedRateLimitStore store)
    {
        var attribute = context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>();
        if (attribute?.PolicyName is null ||
            !RateLimitingExtensions.TryGetPolicy(attribute.PolicyName, out var policy))
        {
            await _next(context);
            return;
        }

        var address = context.Connection.RemoteIpAddress?.ToString() ?? "anon";
        var rawKey = $"{attribute.PolicyName}:{address}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));
        var result = await store.TryAcquireAsync(key, policy.PermitLimit, policy.Window, context.RequestAborted);
        if (result.Acquired)
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json";
        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(result.RetryAfter.TotalSeconds)).ToString();
        await context.Response.WriteAsync(
            """{"success":false,"errorCode":"RATE_LIMIT_EXCEEDED","message":"Too many requests. Please try again later."}""",
            context.RequestAborted);
    }
}
