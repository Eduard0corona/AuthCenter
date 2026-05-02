using AuthCenter.Contracts.Requests.Applications;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class CreateApplicationRequestValidator : AbstractValidator<CreateApplicationRequest>
{
    private static readonly string[] ValidModes = ["Closed", "Open", "InviteOnly", "ApprovalRequired"];

    public CreateApplicationRequestValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Application code is required.")
            .MaximumLength(50)
            .Matches("^[A-Z0-9_]+$").WithMessage("Code must contain only uppercase letters, digits, and underscores.");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Application name is required.")
            .MaximumLength(200);

        RuleFor(x => x.RegistrationMode)
            .NotEmpty()
            .Must(m => ValidModes.Contains(m))
            .WithMessage($"RegistrationMode must be one of: {string.Join(", ", ValidModes)}.");
    }
}
