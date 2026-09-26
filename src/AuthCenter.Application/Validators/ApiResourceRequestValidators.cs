using AuthCenter.Contracts.Requests.ApiResources;
using AuthCenter.Domain.Constants;
using FluentValidation;

namespace AuthCenter.Application.Validators;

internal static class ApiResourceValidationRules
{
    /// <summary>RFC 8707: a resource indicator is an absolute URI without a fragment.</summary>
    internal static bool IsResourceIndicator(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "urn") &&
        string.IsNullOrEmpty(uri.Fragment) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        !value.Any(char.IsWhiteSpace);

    internal static bool IsApiScopeName(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z][a-z0-9_.:-]{1,127}$") &&
        !DomainConstants.OAuthScopes.All.Contains(value, StringComparer.Ordinal);
}

public class ApiScopeRequestValidator : AbstractValidator<ApiScopeRequest>
{
    public ApiScopeRequestValidator()
    {
        RuleFor(x => x.Name)
            .Must(ApiResourceValidationRules.IsApiScopeName)
            .WithMessage("Scope names use lowercase letters, digits, '.', '_', ':' or '-', start with a letter and cannot be an OpenID Connect scope.");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
    }
}

public class CreateApiResourceRequestValidator : AbstractValidator<CreateApiResourceRequest>
{
    public CreateApiResourceRequestValidator()
    {
        RuleFor(x => x.ApplicationSystemId).NotEmpty();
        RuleFor(x => x.Identifier)
            .NotEmpty()
            .MaximumLength(300)
            .Must(ApiResourceValidationRules.IsResourceIndicator)
            .WithMessage("The identifier must be an absolute https: or urn: URI without fragment (RFC 8707).");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Scopes)
            .NotEmpty().WithMessage("An API needs at least one scope.")
            .Must(scopes => scopes.Count <= 100).WithMessage("An API can define at most 100 scopes.")
            .Must(scopes => scopes.Select(scope => scope.Name).Distinct(StringComparer.Ordinal).Count() == scopes.Count)
            .WithMessage("Scope names must be unique.");
        RuleForEach(x => x.Scopes).SetValidator(new ApiScopeRequestValidator());
    }
}

public class UpdateApiResourceRequestValidator : AbstractValidator<UpdateApiResourceRequest>
{
    public UpdateApiResourceRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Scopes)
            .NotEmpty().WithMessage("An API needs at least one scope.")
            .Must(scopes => scopes.Count <= 100).WithMessage("An API can define at most 100 scopes.")
            .Must(scopes => scopes.Select(scope => scope.Name).Distinct(StringComparer.Ordinal).Count() == scopes.Count)
            .WithMessage("Scope names must be unique.");
        RuleForEach(x => x.Scopes).SetValidator(new ApiScopeRequestValidator());
    }
}
