using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Enums;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class CreateOAuthClientRequestValidator : AbstractValidator<CreateOAuthClientRequest>
{
    private static readonly string[] ValidGrantTypes = ["authorization_code", "client_credentials", "refresh_token"];

    public CreateOAuthClientRequestValidator()
    {
        RuleFor(x => x.ClientId)
            .NotEmpty()
            .MaximumLength(100)
            .Matches(@"^[a-z0-9\-_]+$").WithMessage("ClientId must contain only lowercase letters, digits, hyphens, and underscores.");

        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);

        RuleFor(x => x.LoginUrl).NotEmpty().MaximumLength(500);

        RuleFor(x => x.RedirectUris)
            .NotEmpty().WithMessage("At least one redirect URI is required.")
            .When(x => x.GrantTypes.Contains("authorization_code"));

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

        RuleFor(x => x.ClientType)
            .Must(t => Enum.IsDefined(typeof(OAuthClientType), t))
            .WithMessage("ClientType must be 0 (Confidential) or 1 (Public).");

        RuleFor(x => x.RequirePkce)
            .Equal(true)
            .When(x => x.ClientType == (int)OAuthClientType.Public)
            .WithMessage("Public clients must require PKCE.");
    }
}
