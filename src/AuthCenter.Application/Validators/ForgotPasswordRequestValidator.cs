using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Invalid email format.");

        RuleFor(x => x.ApplicationCode)
            .NotEmpty().WithMessage("Application code is required.");
    }
}
