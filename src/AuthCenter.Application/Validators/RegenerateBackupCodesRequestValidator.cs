using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class RegenerateBackupCodesRequestValidator : AbstractValidator<RegenerateBackupCodesRequest>
{
    public RegenerateBackupCodesRequestValidator()
    {
        RuleFor(x => x.TotpCode)
            .NotEmpty()
            .Length(6)
            .Matches("^[0-9]{6}$");
    }
}
