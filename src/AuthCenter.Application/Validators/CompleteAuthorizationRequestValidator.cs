using AuthCenter.Contracts.Requests.OAuth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class CompleteAuthorizationRequestValidator : AbstractValidator<CompleteAuthorizationRequest>
{
    public CompleteAuthorizationRequestValidator()
    {
        RuleFor(x => x.InteractionId).NotEmpty().WithMessage("interaction_id is required.");
    }
}
