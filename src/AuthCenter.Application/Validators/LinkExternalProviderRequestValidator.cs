using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Domain.Constants;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public sealed class LinkExternalProviderRequestValidator : AbstractValidator<LinkExternalProviderRequest>
{
    private static readonly string[] Providers =
    [
        DomainConstants.Providers.Google,
        DomainConstants.Providers.Microsoft,
        DomainConstants.Providers.GitHub,
        DomainConstants.Providers.Apple
    ];

    public LinkExternalProviderRequestValidator()
    {
        RuleFor(x => x.Provider)
            .NotEmpty()
            .Must(provider => Providers.Contains(provider, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Provider is not supported.");
        RuleFor(x => x.Credential).NotEmpty().MaximumLength(20_000);
    }
}
