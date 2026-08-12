using AuthCenter.Contracts.Requests.Roles;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public sealed class SetRolePermissionsRequestValidator : AbstractValidator<SetRolePermissionsRequest>
{
    public SetRolePermissionsRequestValidator()
    {
        RuleFor(request => request.PermissionIds)
            .NotNull()
            .Must(ids => ids.Count <= 500)
            .WithMessage("A role cannot receive more than 500 permissions in one operation.");

        RuleForEach(request => request.PermissionIds)
            .NotEmpty()
            .WithMessage("Permission IDs cannot be empty.");
    }
}
