using System.Net;
using System.Text.Json;
using AuthCenter.Application.Common.Exceptions;
using FluentValidation;

namespace AuthCenter.Api.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, errorCode, message, details) = exception switch
        {
            ValidationException ve => (
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED",
                "One or more validation errors occurred.",
                ve.Errors.Select(e => e.ErrorMessage).ToList()),

            UnauthorizedAccessException => (
                HttpStatusCode.Unauthorized,
                "UNAUTHORIZED",
                "Authentication required.",
                (IList<string>)[]),

            ForbiddenException fe => (
                HttpStatusCode.Forbidden,
                "FORBIDDEN",
                fe.Message,
                (IList<string>)[]),

            NotFoundException nfe => (
                HttpStatusCode.NotFound,
                "NOT_FOUND",
                nfe.Message,
                (IList<string>)[]),

            ConflictException ce => (
                HttpStatusCode.Conflict,
                "CONFLICT",
                ce.Message,
                (IList<string>)[]),

            _ => (
                HttpStatusCode.InternalServerError,
                "INTERNAL_ERROR",
                _env.IsDevelopment() ? exception.Message : "An unexpected error occurred.",
                (IList<string>)[])
        };

        if (statusCode == HttpStatusCode.InternalServerError)
            _logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);

        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";

        var response = new
        {
            success = false,
            errorCode,
            message,
            details = details.Count > 0 ? details : null,
            traceId = System.Diagnostics.Activity.Current?.TraceId.ToHexString() ?? context.TraceIdentifier
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await context.Response.WriteAsync(json);
    }
}
