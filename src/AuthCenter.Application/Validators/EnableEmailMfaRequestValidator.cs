using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class EnableEmailMfaRequestValidator : AbstractValidator<EnableEmailMfaRequest>
{
    public EnableEmailMfaRequestValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .MinimumLength(4)
            .MaximumLength(10);
    }
}
