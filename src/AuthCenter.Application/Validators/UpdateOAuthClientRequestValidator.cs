using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Domain.Constants;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class UpdateOAuthClientRequestValidator : AbstractValidator<UpdateOAuthClientRequest>
{

    public UpdateOAuthClientRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);

        RuleFor(x => x.LoginUrl)
            .NotEmpty()
            .MaximumLength(500)
            .Must(OAuthClientValidationRules.IsSecureBrowserUri)
            .WithMessage("LoginUrl must be an absolute HTTPS URI, or an HTTP loopback URI for local development, without user info or fragment.");

        RuleFor(x => x.RedirectUris)
            .NotEmpty().WithMessage("At least one redirect URI is required.")
            .When(x => x.GrantTypes.Contains("authorization_code"));

        RuleForEach(x => x.RedirectUris)
            .Must(OAuthClientValidationRules.IsSecureBrowserUri)
            .WithMessage("Redirect URIs must use HTTPS, or HTTP loopback for local development, and cannot contain user info or fragments.");

        RuleFor(x => x.RedirectUris)
            .Must(OAuthClientValidationRules.HasUniqueValues)
            .WithMessage("Redirect URIs must be unique.");

        RuleForEach(x => x.PostLogoutRedirectUris)
            .Must(OAuthClientValidationRules.IsSecureBrowserUri)
            .WithMessage("Post-logout redirect URIs must use HTTPS, or HTTP loopback for local development, and cannot contain user info or fragments.");

        RuleFor(x => x.PostLogoutRedirectUris)
            .Must(OAuthClientValidationRules.HasUniqueValues)
            .WithMessage("Post-logout redirect URIs must be unique.")
            .Must(uris => uris.Count <= 20)
            .WithMessage("At most 20 post-logout redirect URIs can be registered.");

        RuleForEach(x => x.AllowedCorsOrigins)
            .Must(OAuthClientValidationRules.IsSecureOrigin)
            .WithMessage("CORS origins are scheme://host[:port] only, using HTTPS or HTTP loopback.");

        RuleFor(x => x.AllowedCorsOrigins)
            .Must(OAuthClientValidationRules.HasUniqueValues)
            .WithMessage("CORS origins must be unique.")
            .Must(origins => origins.Count <= 20)
            .WithMessage("At most 20 CORS origins can be registered.");

        RuleFor(x => x.BackchannelLogoutUri)
            .MaximumLength(500)
            .Must(uri => OAuthClientValidationRules.IsSecureBrowserUri(uri!))
            .When(x => !string.IsNullOrWhiteSpace(x.BackchannelLogoutUri))
            .WithMessage("The back-channel logout URI must use HTTPS, or HTTP loopback for local development, and cannot contain user info or fragments.");

        RuleFor(x => x.BackchannelLogoutUri)
            .Empty()
            .When(x => !x.GrantTypes.Contains("authorization_code"))
            .WithMessage("Back-channel logout only applies to clients that sign users in (authorization_code).");

        RuleFor(x => x.AllowedScopes)
            .NotEmpty().WithMessage("At least one scope must be allowed.")
            .Must(scopes => scopes.All(s => DomainConstants.OAuthScopes.All.Contains(s) || ApiResourceValidationRules.IsApiScopeName(s)))
            .WithMessage($"Allowed scopes must be OpenID Connect scopes ({string.Join(", ", DomainConstants.OAuthScopes.All)}) or API scopes registered in the API catalog.");

        RuleFor(x => x.AllowedScopes)
            .Must(OAuthClientValidationRules.HasUniqueValues)
            .WithMessage("Allowed scopes must be unique.");

        RuleFor(x => x.GrantTypes)
            .NotEmpty().WithMessage("At least one grant type is required.")
            .Must(g => g.All(t => DomainConstants.OAuthGrantTypes.All.Contains(t)))
            .WithMessage($"Grant types must be one of: {string.Join(", ", DomainConstants.OAuthGrantTypes.All)}.");

        RuleFor(x => x.GrantTypes)
            .Must(OAuthClientValidationRules.HasUniqueValues)
            .WithMessage("Grant types must be unique.");

        RuleFor(x => x.RequirePkce)
            .Equal(true)
            .When(x => x.GrantTypes.Contains("authorization_code"))
            .WithMessage("All authorization_code clients must require PKCE.");

        RuleFor(x => x.GrantTypes)
            .Must(g => !g.Contains("refresh_token") || g.Contains("authorization_code"))
            .WithMessage("refresh_token requires authorization_code.");

        RuleFor(x => x.AllowedScopes)
            .Must((request, scopes) => !scopes.Contains(DomainConstants.OAuthScopes.OfflineAccess) ||
                                       (request.GrantTypes.Contains("authorization_code") && request.GrantTypes.Contains("refresh_token")))
            .WithMessage("offline_access requires authorization_code and refresh_token grants.");

        RuleFor(x => x.AccessTokenLifetimeSeconds)
            .InclusiveBetween(60, 3600).WithMessage("Access token lifetime must be between 60 and 3600 seconds.");
    }
}
