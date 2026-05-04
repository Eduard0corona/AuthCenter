using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class VerifyMfaRequestValidator : AbstractValidator<VerifyMfaRequest>
{
    public VerifyMfaRequestValidator()
    {
        RuleFor(x => x.MfaPendingToken).NotEmpty();

        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.TotpCode) ||
                !string.IsNullOrWhiteSpace(x.BackupCode) ||
                !string.IsNullOrWhiteSpace(x.EmailOtpCode))
            .WithMessage("Either TotpCode, BackupCode, or EmailOtpCode is required.");

        When(x => !string.IsNullOrWhiteSpace(x.TotpCode), () =>
        {
            RuleFor(x => x.TotpCode!)
                .Length(6)
                .Matches("^[0-9]{6}$");
        });
    }
}
