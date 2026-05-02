using AuthCenter.Contracts.Requests.Users;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class InviteUserRequestValidator : AbstractValidator<InviteUserRequest>
{
    public InviteUserRequestValidator()
    {
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full name is required.")
            .MaximumLength(200);

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Invalid email format.")
            .MaximumLength(256);

        RuleFor(x => x.ApplicationSystemId)
            .NotEmpty().WithMessage("ApplicationSystemId is required.");
    }
}
