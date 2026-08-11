using System.Diagnostics;
using AuthCenter.Application.Telemetry;

namespace AuthCenter.Api.Middleware;

public sealed class PlatformTelemetryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var operation = Classify(context.Request.Path);
        if (operation is null) { await next(context); return; }
        PlatformTelemetry.RequestStarted(operation);
        var started = Stopwatch.GetTimestamp();
        try { await next(context); }
        finally { PlatformTelemetry.RequestCompleted(operation, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }

    private static string? Classify(PathString path)
    {
        if (path.StartsWithSegments("/api/auth/login") || path.StartsWithSegments("/ui-api/session/login") || path.StartsWithSegments("/api/auth/passkeys/login")) return "login";
        if (path.StartsWithSegments("/oauth/token")) return "token";
        if (path.StartsWithSegments("/api/users") || path.StartsWithSegments("/api/groups") || path.StartsWithSegments("/scim/v2")) return "directory";
        if (path.StartsWithSegments("/api/event-hooks")) return "event_hook_admin";
        return null;
    }
}
