using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Invalid email format.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.");

        RuleFor(x => x.ApplicationCode)
            .NotEmpty().WithMessage("Application code is required.");
    }
}
