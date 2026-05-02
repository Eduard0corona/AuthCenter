using AuthCenter.Application.Validators;
using AuthCenter.Contracts.Requests.Permissions;
using Xunit;

namespace AuthCenter.UnitTests.Validators;

public class CreatePermissionRequestValidatorTests
{
    private readonly CreatePermissionRequestValidator _validator = new();

    [Fact]
    public void Validate_EmptyCode_Fails()
    {
        var result = _validator.Validate(new CreatePermissionRequest
        {
            ApplicationSystemId = Guid.NewGuid(),
            Code = "",
            Name = "Test Permission"
        });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Code");
    }

    [Fact]
    public void Validate_LowercaseCode_Fails()
    {
        var result = _validator.Validate(new CreatePermissionRequest
        {
            ApplicationSystemId = Guid.NewGuid(),
            Code = "invalid_lowercase",
            Name = "Test Permission"
        });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Code");
    }

    [Fact]
    public void Validate_EmptyApplicationSystemId_Fails()
    {
        var result = _validator.Validate(new CreatePermissionRequest
        {
            ApplicationSystemId = Guid.Empty,
            Code = "VALID_CODE",
            Name = "Test Permission"
        });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "ApplicationSystemId");
    }

    [Fact]
    public void Validate_ValidRequest_Passes()
    {
        var result = _validator.Validate(new CreatePermissionRequest
        {
            ApplicationSystemId = Guid.NewGuid(),
            Code = "AUTHCENTER_USERS_READ",
            Name = "Read Users"
        });
        Assert.True(result.IsValid);
    }
}
