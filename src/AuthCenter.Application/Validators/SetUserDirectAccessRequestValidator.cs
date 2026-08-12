using AuthCenter.Contracts.Requests.Users;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public sealed class SetUserDirectAccessRequestValidator : AbstractValidator<SetUserDirectAccessRequest>
{
    public SetUserDirectAccessRequestValidator()
    {
        RuleFor(request => request.ApplicationSystemIds)
            .NotNull()
            .Must(ids => ids.Count <= 100).WithMessage("A user cannot receive more than 100 direct applications in one operation.")
            .Must(ids => ids.All(id => id != Guid.Empty)).WithMessage("Application identifiers cannot be empty.");
        RuleFor(request => request.RoleIds)
            .NotNull()
            .Must(ids => ids.Count <= 500).WithMessage("A user cannot receive more than 500 direct roles in one operation.")
            .Must(ids => ids.All(id => id != Guid.Empty)).WithMessage("Role identifiers cannot be empty.");
    }
}
