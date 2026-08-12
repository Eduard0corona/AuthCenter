using AuthCenter.Contracts.Requests.Groups;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public sealed class SetDirectoryGroupAccessRequestValidator : AbstractValidator<SetDirectoryGroupAccessRequest>
{
    public SetDirectoryGroupAccessRequestValidator()
    {
        RuleFor(request => request.ApplicationSystemIds)
            .NotNull()
            .Must(ids => ids.Count <= 100)
            .WithMessage("A group cannot receive more than 100 applications in one operation.");
        RuleFor(request => request.RoleIds)
            .NotNull()
            .Must(ids => ids.Count <= 500)
            .WithMessage("A group cannot receive more than 500 roles in one operation.");
        RuleForEach(request => request.ApplicationSystemIds).NotEmpty();
        RuleForEach(request => request.RoleIds).NotEmpty();
    }
}
