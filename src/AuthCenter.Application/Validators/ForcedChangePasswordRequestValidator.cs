using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class ForcedChangePasswordRequestValidator : AbstractValidator<ForcedChangePasswordRequest>
{
    public ForcedChangePasswordRequestValidator()
    {
        RuleFor(x => x.ForcedChangePendingToken).NotEmpty();
        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(8)
            .Matches("[A-Z]")
            .Matches("[a-z]")
            .Matches("[0-9]");
    }
}
