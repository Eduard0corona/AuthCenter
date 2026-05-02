using AuthCenter.Contracts.Requests.Users;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full name is required.")
            .MaximumLength(200);

        When(x => x.PictureUrl is not null, () =>
        {
            RuleFor(x => x.PictureUrl!)
                .MaximumLength(2048).WithMessage("Picture URL must not exceed 2048 characters.")
                .Must(url => Uri.TryCreate(url, UriKind.Absolute, out _)).WithMessage("Picture URL must be a valid absolute URI.");
        });
    }
}
