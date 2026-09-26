using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class DisableMfaRequestValidator : AbstractValidator<DisableMfaRequest>
{
    public DisableMfaRequestValidator()
    {
        // An email factor has no authenticator or backup codes: it is confirmed with an emailed code.
        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.TotpCode) || !string.IsNullOrWhiteSpace(x.BackupCode) || !string.IsNullOrWhiteSpace(x.EmailOtpCode))
            .WithMessage("A TotpCode, BackupCode or EmailOtpCode is required.");

        When(x => !string.IsNullOrWhiteSpace(x.TotpCode), () =>
        {
            RuleFor(x => x.TotpCode!)
                .Length(6)
                .Matches("^[0-9]{6}$");
        });

        When(x => !string.IsNullOrWhiteSpace(x.EmailOtpCode), () =>
        {
            RuleFor(x => x.EmailOtpCode!)
                .Length(6)
                .Matches("^[0-9]{6}$");
        });

        When(x => !string.IsNullOrWhiteSpace(x.BackupCode), () =>
        {
            RuleFor(x => x.BackupCode).NotEmpty();
        });
    }
}
