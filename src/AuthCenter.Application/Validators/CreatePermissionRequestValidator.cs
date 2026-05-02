using AuthCenter.Contracts.Requests.Permissions;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class CreatePermissionRequestValidator : AbstractValidator<CreatePermissionRequest>
{
    public CreatePermissionRequestValidator()
    {
        RuleFor(x => x.ApplicationSystemId)
            .NotEmpty().WithMessage("Application system ID is required.");

        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Permission code is required.")
            .MaximumLength(100)
            .Matches("^[A-Z0-9_]+$").WithMessage("Code must contain only uppercase letters, digits, and underscores.");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Permission name is required.")
            .MaximumLength(200);

        RuleFor(x => x.Description)
            .MaximumLength(500)
            .When(x => x.Description is not null);
    }
}
