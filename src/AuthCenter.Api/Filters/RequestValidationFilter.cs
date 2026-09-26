using AuthCenter.Contracts.Responses;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AuthCenter.Api.Filters;

/// <summary>
/// Runs the FluentValidation validator registered for every bound action argument before the
/// action executes, so request contracts are enforced at the API boundary instead of relying on
/// each service or on the administrative UI.
/// </summary>
public sealed partial class RequestValidationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
                continue;

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (context.HttpContext.RequestServices.GetService(validatorType) is not IValidator validator)
                continue;

            var result = await validator.ValidateAsync(
                new ValidationContext<object>(argument),
                context.HttpContext.RequestAborted);
            if (result.IsValid)
                continue;

            // A rule can keep a documented, stable error code (for example DELETION_NOT_CONFIRMED)
            // with WithErrorCode; FluentValidation's own default codes are validator type names.
            var stableCode = result.Errors
                .Select(error => error.ErrorCode)
                .FirstOrDefault(code => code is not null && StableErrorCode().IsMatch(code));
            context.Result = new BadRequestObjectResult(ApiResponse<object>.Fail(
                stableCode ?? "VALIDATION_FAILED",
                stableCode is null ? "One or more validation errors occurred." : result.Errors[0].ErrorMessage,
                result.Errors.Select(error => error.ErrorMessage).Distinct(StringComparer.Ordinal).ToList()));
            return;
        }

        await next();
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Z][A-Z0-9_]+$")]
    private static partial System.Text.RegularExpressions.Regex StableErrorCode();
}
