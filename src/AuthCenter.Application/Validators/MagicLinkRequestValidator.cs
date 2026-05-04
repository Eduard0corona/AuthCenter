using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class MagicLinkRequestValidator : AbstractValidator<MagicLinkRequest>
{
    public MagicLinkRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.ApplicationCode).NotEmpty();
    }
}
