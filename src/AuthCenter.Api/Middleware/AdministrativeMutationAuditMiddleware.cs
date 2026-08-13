using AuthCenter.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace AuthCenter.Api.Middleware;

public sealed class AdministrativeMutationAuditMiddleware(RequestDelegate next, ILogger<AdministrativeMutationAuditMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IAuditService audit)
    {
        await next(context);
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method) || context.Response.StatusCode < 400)
            return;
        var endpoint = context.GetEndpoint();
        var isAdministrative = endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(item => item.Policy?.StartsWith("AUTHCENTER_", StringComparison.Ordinal) == true) == true;
        if (!isAdministrative) return;
        try
        {
            await audit.LogAsync("ADMIN_MUTATION_REJECTED", entityName: "AdminApiOperation", metadata: new
            {
                result = "Rejected",
                method = context.Request.Method,
                endpoint = endpoint?.DisplayName,
                statusCode = context.Response.StatusCode
            }, ct: context.RequestAborted);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not audit rejected administrative mutation {Method} {Path}", context.Request.Method, context.Request.Path);
        }
    }
}
