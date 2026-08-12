using AuthCenter.Contracts.Requests.Users;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full name is required.")
            .MaximumLength(200);

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Invalid email format.")
            .MaximumLength(256);

        When(x => x.Password is not null, () =>
        {
            RuleFor(x => x.Password!)
                .MinimumLength(8).WithMessage("Password must be at least 8 characters.")
                .Matches("[A-Z]").WithMessage("Password must contain at least one uppercase letter.")
                .Matches("[a-z]").WithMessage("Password must contain at least one lowercase letter.")
                .Matches("[0-9]").WithMessage("Password must contain at least one digit.");
        });

        When(x => x.IsTemporaryPassword, () =>
        {
            RuleFor(x => x.Password)
                .NotEmpty().WithMessage("A temporary password is required when IsTemporaryPassword is true.")
                .MinimumLength(12).WithMessage("A temporary password must be at least 12 characters.");
        });

        When(x => x.GrantApplicationAccess, () =>
        {
            RuleFor(x => x.ApplicationSystemId)
                .NotEmpty().WithMessage("ApplicationSystemId is required when granting application access.");
        });
    }
}
