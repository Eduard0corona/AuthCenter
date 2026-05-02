using AuthCenter.Application.Validators;
using AuthCenter.Contracts.Requests.Auth;
using Xunit;

namespace AuthCenter.UnitTests.Validators;

public class LoginRequestValidatorTests
{
    private readonly LoginRequestValidator _validator = new();

    [Fact]
    public void Validate_EmptyEmail_Fails()
    {
        var result = _validator.Validate(new LoginRequest { Email = "", Password = "Pass123!", ApplicationCode = "APP" });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Email");
    }

    [Fact]
    public void Validate_InvalidEmail_Fails()
    {
        var result = _validator.Validate(new LoginRequest { Email = "not-an-email", Password = "Pass123!", ApplicationCode = "APP" });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Email");
    }

    [Fact]
    public void Validate_EmptyPassword_Fails()
    {
        var result = _validator.Validate(new LoginRequest { Email = "user@test.com", Password = "", ApplicationCode = "APP" });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Password");
    }

    [Fact]
    public void Validate_EmptyApplicationCode_Fails()
    {
        var result = _validator.Validate(new LoginRequest { Email = "user@test.com", Password = "Pass123!", ApplicationCode = "" });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "ApplicationCode");
    }

    [Fact]
    public void Validate_ValidRequest_Passes()
    {
        var result = _validator.Validate(new LoginRequest { Email = "user@test.com", Password = "Pass123!", ApplicationCode = "AUTHCENTER" });
        Assert.True(result.IsValid);
    }
}
