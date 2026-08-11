using AuthCenter.Contracts.Requests.Groups;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class UpdateDirectoryGroupRequestValidator : AbstractValidator<UpdateDirectoryGroupRequest>
{
    public UpdateDirectoryGroupRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
        RuleFor(request => request.Description).MaximumLength(1000);
    }
}
