using AuthCenter.Contracts.Responses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AuthCenter.Api.Filters;

/// <summary>
/// A stale update (<c>CONCURRENCY_CONFLICT</c>) is a 409 on every endpoint, even where the action
/// answers any failed operation with 400: clients reload instead of treating it as bad input.
/// </summary>
public sealed class ConcurrencyConflictResultFilter : IAlwaysRunResultFilter
{
    public const string ConflictCode = "CONCURRENCY_CONFLICT";

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ObjectResult result)
            result.StatusCode = EffectiveStatus(result);
    }

    /// <summary>The status the response will have: a 400 carrying <c>CONCURRENCY_CONFLICT</c> becomes 409.</summary>
    public static int EffectiveStatus(ObjectResult result)
    {
        var status = result.StatusCode ?? StatusCodes.Status200OK;
        return status == StatusCodes.Status400BadRequest && ErrorCode(result.Value) == ConflictCode ? StatusCodes.Status409Conflict : status;
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    private static string? ErrorCode(object? value) => value switch
    {
        ApiResponse response => response.ErrorCode,
        ApiResponse<object> response => response.ErrorCode,
        null => null,
        _ => value.GetType().GetProperty(nameof(ApiResponse.ErrorCode))?.GetValue(value) as string
    };
}
