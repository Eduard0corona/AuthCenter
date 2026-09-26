using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AuthCenter.Api.Services;

/// <summary>
/// The readiness report: each check's status and description, so an operator sees what is not ready
/// (pending migrations, SQL unreachable). Exceptions and connection details are never written.
/// </summary>
public static class HealthCheckResponses
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task WriteReadinessAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Exception is null ? entry.Value.Description : "The check failed."
            })
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }
}
