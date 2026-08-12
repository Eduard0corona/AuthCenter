using AuthCenter.Contracts.Requests.Permissions;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public sealed class UpdatePermissionRequestValidator : AbstractValidator<UpdatePermissionRequest>
{
    public UpdatePermissionRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
        RuleFor(request => request.Description).MaximumLength(500).When(request => request.Description is not null);
    }
}
