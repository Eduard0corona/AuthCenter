using AuthCenter.Contracts.Requests.Auth;
using FluentValidation;

namespace AuthCenter.Application.Validators;

public class SendMfaEmailOtpRequestValidator : AbstractValidator<SendMfaEmailOtpRequest>
{
    public SendMfaEmailOtpRequestValidator()
    {
        RuleFor(x => x.MfaPendingToken).NotEmpty();
    }
}
