using AuthCenter.Contracts.Requests.Roles;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public sealed class UpdateRoleRequestValidator : AbstractValidator<UpdateRoleRequest>
{
    public UpdateRoleRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(256);
        RuleFor(request => request.Description).MaximumLength(500).When(request => request.Description is not null);
    }
}
