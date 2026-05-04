using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class EnableMfaRequestValidator : AbstractValidator<EnableMfaRequest>
{
    public EnableMfaRequestValidator()
    {
        RuleFor(x => x.TotpCode)
            .NotEmpty()
            .Length(6)
            .Matches("^[0-9]{6}$");
    }
}
