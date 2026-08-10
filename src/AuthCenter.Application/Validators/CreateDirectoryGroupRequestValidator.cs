using AuthCenter.Contracts.Requests.Groups;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class CreateDirectoryGroupRequestValidator : AbstractValidator<CreateDirectoryGroupRequest>
{
    public CreateDirectoryGroupRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
        RuleFor(request => request.Description).MaximumLength(1000);
    }
}
