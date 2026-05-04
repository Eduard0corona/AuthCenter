using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class DisableMfaRequestValidator : AbstractValidator<DisableMfaRequest>
{
    public DisableMfaRequestValidator()
    {
        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.TotpCode) || !string.IsNullOrWhiteSpace(x.BackupCode))
            .WithMessage("Either TotpCode or BackupCode is required.");

        When(x => !string.IsNullOrWhiteSpace(x.TotpCode), () =>
        {
            RuleFor(x => x.TotpCode!)
                .Length(6)
                .Matches("^[0-9]{6}$");
        });

        When(x => !string.IsNullOrWhiteSpace(x.BackupCode), () =>
        {
            RuleFor(x => x.BackupCode).NotEmpty();
        });
    }
}
