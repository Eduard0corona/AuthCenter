using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthCenter.Contracts.Requests.Applications;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Profiles;
using AuthCenter.Contracts.Requests.Users;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Applications;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Profiles;
using AuthCenter.Contracts.Responses.Users;
using AuthCenter.Domain.Constants;

namespace AuthCenter.IntegrationTests;

public sealed class UserProfileSchemaTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public UserProfileSchemaTests(AuthCenterWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TypedSchema_ProvidesDefaults_ValidatesProfiles_AndProtectsExistingValues()
    {
        using var admin = await CreateAdminClientAsync();

        var missingDefault = await admin.PostAsJsonAsync("/api/profile-schema", new CreateProfileAttributeDefinitionRequest
        {
            Key = $"required.{Guid.NewGuid():N}",
            DisplayName = "Required without default",
            DataType = "String",
            IsRequired = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingDefault.StatusCode);
        Assert.Equal(
            "PROFILE_REQUIRED_DEFAULT_MISSING",
            (await missingDefault.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);

        var departmentKey = $"department.{Guid.NewGuid():N}";
        var department = await ReadDataAsync<ProfileAttributeDefinitionDto>(await admin.PostAsJsonAsync(
            "/api/profile-schema",
            new CreateProfileAttributeDefinitionRequest
            {
                Key = departmentKey,
                DisplayName = "Department",
                DataType = "String",
                IsRequired = true,
                DefaultValue = JsonSerializer.SerializeToElement("general"),
                MinLength = 2,
                MaxLength = 40,
                ValidationPattern = "^[a-z]+$",
                AllowedValues =
                [
                    JsonSerializer.SerializeToElement("general"),
                    JsonSerializer.SerializeToElement("engineering")
                ]
            }));

        var ageKey = $"age.{Guid.NewGuid():N}";
        var age = await ReadDataAsync<ProfileAttributeDefinitionDto>(await admin.PostAsJsonAsync(
            "/api/profile-schema",
            new CreateProfileAttributeDefinitionRequest
            {
                Key = ageKey,
                DisplayName = "Age",
                DataType = "Integer",
                MinimumNumber = 18,
                MaximumNumber = 70
            }));

        var application = await CreateApplicationAsync(admin);
        var user = await CreateUserAsync(admin, application.Id);
        var initial = await ReadDataAsync<UserProfileDto>(await admin.GetAsync($"/api/users/{user.Id}/profile"));
        Assert.True(initial.IsValid);
        var defaultDepartment = Assert.Single(initial.Attributes, attribute => attribute.Key == departmentKey);
        Assert.True(defaultDepartment.IsDefault);
        Assert.Equal("general", defaultDepartment.Value.GetString());

        var updated = await ReadDataAsync<UserProfileDto>(await admin.PutAsJsonAsync(
            $"/api/users/{user.Id}/profile",
            new UpdateUserProfileRequest
            {
                Attributes = new Dictionary<string, JsonElement?>
                {
                    [departmentKey] = JsonSerializer.SerializeToElement("engineering"),
                    [ageKey] = JsonSerializer.SerializeToElement(33)
                }
            }));
        Assert.True(updated.IsValid);
        Assert.False(Assert.Single(updated.Attributes, attribute => attribute.Key == departmentKey).IsDefault);
        Assert.Equal(33, Assert.Single(updated.Attributes, attribute => attribute.Key == ageKey).Value.GetInt32());

        var invalidAge = await admin.PutAsJsonAsync(
            $"/api/users/{user.Id}/profile",
            new UpdateUserProfileRequest
            {
                Attributes = new Dictionary<string, JsonElement?>
                {
                    [ageKey] = JsonSerializer.SerializeToElement(12)
                }
            });
        Assert.Equal(HttpStatusCode.BadRequest, invalidAge.StatusCode);
        Assert.Equal(
            "PROFILE_VALUE_OUT_OF_RANGE",
            (await invalidAge.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);

        // A number in quotes is text, refused like any other mismatch (it used to fail with a 500).
        var quotedAge = await admin.PutAsJsonAsync(
            $"/api/users/{user.Id}/profile",
            new UpdateUserProfileRequest
            {
                Attributes = new Dictionary<string, JsonElement?>
                {
                    [ageKey] = JsonSerializer.SerializeToElement("33")
                }
            });
        Assert.Equal(HttpStatusCode.BadRequest, quotedAge.StatusCode);
        Assert.Equal(
            "PROFILE_VALUE_TYPE_MISMATCH",
            (await quotedAge.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);

        var breakingSchemaChange = await admin.PutAsJsonAsync(
            $"/api/profile-schema/{department.Id}",
            new UpdateProfileAttributeDefinitionRequest
            {
                DisplayName = "Department",
                DataType = "String",
                IsRequired = true,
                DefaultValue = JsonSerializer.SerializeToElement("general"),
                AllowedValues = [JsonSerializer.SerializeToElement("general")]
            });
        Assert.Equal(HttpStatusCode.BadRequest, breakingSchemaChange.StatusCode);
        Assert.Equal(
            "PROFILE_EXISTING_VALUES_INVALID",
            (await breakingSchemaChange.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/profile-schema/{age.Id}")).StatusCode);
        var activeSchema = await ReadDataAsync<List<ProfileAttributeDefinitionDto>>(await admin.GetAsync("/api/profile-schema"));
        Assert.DoesNotContain(activeSchema, definition => definition.Id == age.Id);
        var fullSchema = await ReadDataAsync<List<ProfileAttributeDefinitionDto>>(await admin.GetAsync("/api/profile-schema?includeInactive=true"));
        Assert.Contains(fullSchema, definition => definition.Id == age.Id && !definition.IsActive);
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateClient();
        var auth = await ReadDataAsync<AuthResponse>(await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private static async Task<ApplicationDto> CreateApplicationAsync(HttpClient admin)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        return await ReadDataAsync<ApplicationDto>(await admin.PostAsJsonAsync("/api/applications", new CreateApplicationRequest
        {
            Code = $"PRO{suffix}",
            Name = $"Profile test {suffix}",
            RegistrationMode = "InviteOnly",
            AllowPasswordLogin = true
        }));
    }

    private static Task<UserDto> CreateUserAsync(HttpClient admin, Guid applicationId) =>
        ReadDataAsync<UserDto>(admin.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            FullName = "Profile User",
            Email = $"profile-{Guid.NewGuid():N}@example.com",
            Password = TestSecretGenerator.CreatePassword(),
            ApplicationSystemId = applicationId,
            GrantApplicationAccess = true
        }));

    private static async Task<T> ReadDataAsync<T>(Task<HttpResponseMessage> responseTask) =>
        await ReadDataAsync<T>(await responseTask);

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        Assert.NotNull(body);
        Assert.True(body.Success, $"Expected success but got {body.ErrorCode}: {body.Message}");
        Assert.NotNull(body.Data);
        return body.Data;
    }
}
