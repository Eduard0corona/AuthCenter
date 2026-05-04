using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class GitHubLoginRequestValidator : AbstractValidator<GitHubLoginRequest>
{
    public GitHubLoginRequestValidator()
    {
        RuleFor(x => x.AccessToken).NotEmpty();
        RuleFor(x => x.ApplicationCode).NotEmpty();
    }
}
