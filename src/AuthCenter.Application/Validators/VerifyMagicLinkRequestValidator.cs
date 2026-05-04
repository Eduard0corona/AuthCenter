using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class VerifyMagicLinkRequestValidator : AbstractValidator<VerifyMagicLinkRequest>
{
    public VerifyMagicLinkRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
        RuleFor(x => x.ApplicationCode).NotEmpty();
    }
}
