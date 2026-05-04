using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class AppleLoginRequestValidator : AbstractValidator<AppleLoginRequest>
{
    public AppleLoginRequestValidator()
    {
        RuleFor(x => x.IdToken).NotEmpty();
        RuleFor(x => x.ApplicationCode).NotEmpty();
    }
}
