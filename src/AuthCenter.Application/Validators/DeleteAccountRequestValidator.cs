using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class DeleteAccountRequestValidator : AbstractValidator<DeleteAccountRequest>
{
    public DeleteAccountRequestValidator()
    {
        RuleFor(x => x.ConfirmDeletion)
            .Equal(true)
            .WithErrorCode("DELETION_NOT_CONFIRMED")
            .WithMessage("Account deletion must be explicitly confirmed.");
    }
}
