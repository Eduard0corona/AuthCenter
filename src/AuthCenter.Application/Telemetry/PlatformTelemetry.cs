using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AuthCenter.Application.Telemetry;

public static class PlatformTelemetry
{
    public const string SourceName = "AuthCenter.Platform";
    public const string MeterName = "AuthCenter.Platform";
    public static readonly ActivitySource ActivitySource = new(SourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("authcenter.operation.requests", "{request}");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("authcenter.operation.duration", "ms");
    private static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>("authcenter.operation.active", "{request}");
    private static readonly Counter<long> HookDeliveries = Meter.CreateCounter<long>("authcenter.event_hook.deliveries", "{delivery}");

    public static void RequestStarted(string operation) => Active.Add(1, new KeyValuePair<string, object?>("authcenter.operation", operation));

    public static void RequestCompleted(string operation, int statusCode, double elapsedMilliseconds)
    {
        var outcome = statusCode < 400 ? "success" : statusCode < 500 ? "client_error" : "server_error";
        TagList tags = default;
        tags.Add("authcenter.operation", operation);
        tags.Add("authcenter.outcome", outcome);
        Requests.Add(1, tags);
        Duration.Record(elapsedMilliseconds, tags);
        Active.Add(-1, new KeyValuePair<string, object?>("authcenter.operation", operation));
    }

    public static void EventHookCompleted(string outcome, int attempt, double elapsedMilliseconds)
    {
        TagList tags = default;
        tags.Add("authcenter.outcome", outcome);
        tags.Add("authcenter.attempt_band", attempt <= 1 ? "first" : attempt < 10 ? "retry" : "terminal");
        HookDeliveries.Add(1, tags);
        Duration.Record(elapsedMilliseconds, new TagList { { "authcenter.operation", "event_hook" }, { "authcenter.outcome", outcome } });
    }
}
