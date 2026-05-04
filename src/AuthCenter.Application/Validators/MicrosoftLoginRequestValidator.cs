using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class MicrosoftLoginRequestValidator : AbstractValidator<MicrosoftLoginRequest>
{
    public MicrosoftLoginRequestValidator()
    {
        RuleFor(x => x.IdToken).NotEmpty();
        RuleFor(x => x.ApplicationCode).NotEmpty();
    }
}
