using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Domain.Constants;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class UpdateOAuthClientRequestValidator : AbstractValidator<UpdateOAuthClientRequest>
{
    private static readonly string[] ValidGrantTypes = ["authorization_code", "client_credentials", "refresh_token"];

    public UpdateOAuthClientRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);

        RuleFor(x => x.LoginUrl).NotEmpty().MaximumLength(500);

        RuleFor(x => x.AllowedScopes)
            .NotEmpty().WithMessage("At least one scope must be allowed.")
            .Must(scopes => scopes.All(s => DomainConstants.OAuthScopes.All.Contains(s)))
            .WithMessage($"Allowed scopes must be a subset of: {string.Join(", ", DomainConstants.OAuthScopes.All)}");

        RuleFor(x => x.GrantTypes)
            .NotEmpty().WithMessage("At least one grant type is required.")
            .Must(g => g.All(t => ValidGrantTypes.Contains(t)))
            .WithMessage("Grant types must be one of: authorization_code, client_credentials, refresh_token.");

        RuleFor(x => x.AccessTokenLifetimeSeconds)
            .InclusiveBetween(60, 86400).WithMessage("Access token lifetime must be between 60 and 86400 seconds.");
    }
}
