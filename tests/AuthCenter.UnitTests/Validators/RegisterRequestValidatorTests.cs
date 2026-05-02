using AuthCenter.Application.Validators;
using AuthCenter.Contracts.Requests.Auth;
using Xunit;

namespace AuthCenter.UnitTests.Validators;

public class RegisterRequestValidatorTests
{
    private readonly RegisterRequestValidator _validator = new();

    [Fact]
    public void Validate_InvalidEmailFormat_Fails()
    {
        var result = _validator.Validate(new RegisterRequest
        {
            FullName = "Test User",
            Email = "not-an-email",
            Password = "Password1",
            ApplicationCode = "APP"
        });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Email");
    }

    [Fact]
    public void Validate_PasswordTooShort_Fails()
    {
        var result = _validator.Validate(new RegisterRequest
        {
            FullName = "Test User",
            Email = "user@test.com",
            Password = "Ab1",
            ApplicationCode = "APP"
        });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Password");
    }

    [Fact]
    public void Validate_PasswordNoUppercase_Fails()
    {
        var result = _validator.Validate(new RegisterRequest
        {
            FullName = "Test User",
            Email = "user@test.com",
            Password = "password1",
            ApplicationCode = "APP"
        });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Password");
    }

    [Fact]
    public void Validate_PasswordNoDigit_Fails()
    {
        var result = _validator.Validate(new RegisterRequest
        {
            FullName = "Test User",
            Email = "user@test.com",
            Password = "PasswordOnly",
            ApplicationCode = "APP"
        });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Password");
    }

    [Fact]
    public void Validate_ValidRequest_Passes()
    {
        var result = _validator.Validate(new RegisterRequest
        {
            FullName = "Test User",
            Email = "user@test.com",
            Password = "Password1",
            ApplicationCode = "AUTHCENTER"
        });
        Assert.True(result.IsValid);
    }
}
