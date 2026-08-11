using AuthCenter.Application.Telemetry;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AuthCenter.Api.Extensions;

public static class ObservabilityExtensions
{
    public static IServiceCollection AddPlatformObservability(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var serviceName = configuration["OpenTelemetry:ServiceName"] ?? "AuthCenter.Api";
        var azureMonitorConnection = configuration["AzureMonitor:ConnectionString"];
        var exportToAzureMonitor = !environment.IsEnvironment("Testing") &&
            !string.IsNullOrWhiteSpace(azureMonitorConnection) &&
            !azureMonitorConnection.Contains("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase);
        var telemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()).AddAttributes([
                new KeyValuePair<string, object>("deployment.environment.name", environment.EnvironmentName)
            ]))
            .WithTracing(tracing =>
            {
                tracing.AddSource(PlatformTelemetry.SourceName)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health/live");
                    })
                    .AddHttpClientInstrumentation(options => options.RecordException = true);
                if (exportToAzureMonitor)
                    tracing.AddAzureMonitorTraceExporter(options => options.ConnectionString = azureMonitorConnection);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(PlatformTelemetry.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (exportToAzureMonitor)
                    metrics.AddAzureMonitorMetricExporter(options => options.ConnectionString = azureMonitorConnection);
            });
        return services;
    }
}
