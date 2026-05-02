using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class GoogleLoginRequestValidator : AbstractValidator<GoogleLoginRequest>
{
    public GoogleLoginRequestValidator()
    {
        RuleFor(x => x.IdToken)
            .NotEmpty().WithMessage("Google ID token is required.");

        RuleFor(x => x.ApplicationCode)
            .NotEmpty().WithMessage("Application code is required.");
    }
}
