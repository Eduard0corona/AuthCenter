using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AuthCenter.Contracts.Requests.Applications;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Requests.Profiles;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Applications;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Lifecycle;
using AuthCenter.Contracts.Responses.Profiles;
using AuthCenter.Domain.Constants;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// ADM-07: the SCIM 2.0 protocol as identity providers use it — discovery, PUT, the PATCH shapes of
/// Entra ID and Okta, sorting and paging, ETags, attribute projection, mapped extension attributes and
/// the per-token diagnostics shown in the console.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class ScimProtocolTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    private const string GroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";
    private const string EnterpriseSchema = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";
    private const string PatchSchema = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    private static readonly string[] AllScopes = ["scim.users.read", "scim.users.write", "scim.groups.read", "scim.groups.write"];
    private readonly AuthCenterWebApplicationFactory _factory;

    public ScimProtocolTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Discovery_DescribesTheServiceItsResourceTypesAndSchemas()
    {
        using var client = _factory.CreateClient();

        var config = await JsonAsync(await client.GetAsync("/scim/v2/ServiceProviderConfig"));
        Assert.True(config.GetProperty("patch").GetProperty("supported").GetBoolean());
        Assert.True(config.GetProperty("sort").GetProperty("supported").GetBoolean());
        Assert.True(config.GetProperty("etag").GetProperty("supported").GetBoolean());
        Assert.False(config.GetProperty("bulk").GetProperty("supported").GetBoolean());
        Assert.Equal(200, config.GetProperty("filter").GetProperty("maxResults").GetInt32());
        Assert.Equal("oauthbearertoken", config.GetProperty("authenticationSchemes")[0].GetProperty("type").GetString());

        var types = await JsonAsync(await client.GetAsync("/scim/v2/ResourceTypes"));
        Assert.Equal(2, types.GetProperty("totalResults").GetInt32());
        var userType = await JsonAsync(await client.GetAsync("/scim/v2/ResourceTypes/User"));
        Assert.Equal("/Users", userType.GetProperty("endpoint").GetString());
        Assert.Equal(EnterpriseSchema, userType.GetProperty("schemaExtensions")[0].GetProperty("schema").GetString());
        Assert.EndsWith("/scim/v2/ResourceTypes/User", userType.GetProperty("meta").GetProperty("location").GetString());

        var schemas = await JsonAsync(await client.GetAsync("/scim/v2/Schemas"));
        Assert.Equal(3, schemas.GetProperty("totalResults").GetInt32());
        var user = await JsonAsync(await client.GetAsync($"/scim/v2/Schemas/{UserSchema}"));
        var userName = user.GetProperty("attributes").EnumerateArray().Single(attribute => attribute.GetProperty("name").GetString() == "userName");
        Assert.True(userName.GetProperty("required").GetBoolean());
        Assert.Equal("server", userName.GetProperty("uniqueness").GetString());
        var members = (await JsonAsync(await client.GetAsync($"/scim/v2/Schemas/{GroupSchema}"))).GetProperty("attributes").EnumerateArray()
            .Single(attribute => attribute.GetProperty("name").GetString() == "members");
        Assert.True(members.GetProperty("multiValued").GetBoolean());

        var missing = await client.GetAsync("/scim/v2/Schemas/urn:example:unknown");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/scim+json", missing.Content.Headers.ContentType?.MediaType);
        Assert.Equal("404", (await JsonAsync(missing, HttpStatusCode.NotFound)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Users_AreCreatedReplacedAndPatched_AsIdentityProvidersSendThem()
    {
        var tenant = await CreateTenantAsync(AllScopes);
        var email = $"ada-{Guid.NewGuid():N}@example.com";

        var created = await SendAsync(tenant.Scim, HttpMethod.Post, "/scim/v2/Users", new
        {
            schemas = new[] { UserSchema }, userName = email, externalId = "ext-1", active = true,
            name = new { givenName = "Ada", familyName = "Lovelace" }
        });
        var user = await JsonAsync(created, HttpStatusCode.Created);
        var id = user.GetProperty("id").GetString()!;
        Assert.EndsWith($"/scim/v2/Users/{id}", created.Headers.Location?.ToString());
        Assert.Equal(created.Headers.Location?.ToString(), user.GetProperty("meta").GetProperty("location").GetString());
        Assert.Equal(created.Headers.ETag?.ToString(), user.GetProperty("meta").GetProperty("version").GetString());
        Assert.Equal("Ada Lovelace", user.GetProperty("displayName").GetString());
        Assert.Equal(email, user.GetProperty("emails")[0].GetProperty("value").GetString());
        Assert.Equal("ext-1", user.GetProperty("externalId").GetString());

        // PUT replaces the resource: externalId is gone; active, left out, does not change.
        var replaced = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Put, $"/scim/v2/Users/{id}", new { schemas = new[] { UserSchema }, userName = email, displayName = "Ada King" }));
        Assert.Equal("Ada King", replaced.GetProperty("displayName").GetString());
        Assert.False(replaced.TryGetProperty("externalId", out _));
        Assert.True(replaced.GetProperty("active").GetBoolean());

        // Entra ID: no path, a capitalized op and active as text.
        var entra = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Users/{id}", Patch(new
        {
            op = "Replace", value = new Dictionary<string, object> { ["active"] = "False", ["displayName"] = "Ada, Countess of Lovelace" }
        })));
        Assert.False(entra.GetProperty("active").GetBoolean());
        Assert.Equal("Ada, Countess of Lovelace", entra.GetProperty("displayName").GetString());

        // Okta: one operation per path.
        var okta = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Users/{id}", Patch(
            new { op = "replace", path = "active", value = (object)true },
            new { op = "add", path = "externalId", value = (object)"ext-2" })));
        Assert.True(okta.GetProperty("active").GetBoolean());
        Assert.Equal("ext-2", okta.GetProperty("externalId").GetString());

        var other = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Post, "/scim/v2/Users", new { userName = $"other-{Guid.NewGuid():N}@example.com" }), HttpStatusCode.Created);
        var taken = await SendAsync(tenant.Scim, HttpMethod.Put, $"/scim/v2/Users/{id}", new { userName = other.GetProperty("userName").GetString() });
        Assert.Equal("uniqueness", (await JsonAsync(taken, HttpStatusCode.Conflict)).GetProperty("scimType").GetString());
        var required = await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Users/{id}", Patch(new { op = "remove", path = "userName" }));
        Assert.Equal("mutability", (await JsonAsync(required, HttpStatusCode.BadRequest)).GetProperty("scimType").GetString());
        var invalidPath = await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Users/{id}", Patch(new { op = "replace", path = "name..given", value = "x" }));
        Assert.Equal("invalidPath", (await JsonAsync(invalidPath, HttpStatusCode.BadRequest)).GetProperty("scimType").GetString());
    }

    [Fact]
    public async Task ETags_RefuseLostUpdates_AndSpareUnchangedReads()
    {
        var tenant = await CreateTenantAsync(AllScopes);
        var created = await SendAsync(tenant.Scim, HttpMethod.Post, "/scim/v2/Users", new { userName = $"etag-{Guid.NewGuid():N}@example.com" });
        var id = (await JsonAsync(created, HttpStatusCode.Created)).GetProperty("id").GetString();
        var first = created.Headers.ETag!.ToString();
        Assert.StartsWith("W/\"", first);

        Assert.Equal(HttpStatusCode.NotModified, (await SendAsync(tenant.Scim, HttpMethod.Get, $"/scim/v2/Users/{id}", ifNoneMatch: first)).StatusCode);
        var changed = await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Users/{id}", Patch(new { op = "replace", path = "displayName", value = "Renamed" }), ifMatch: first);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var second = changed.Headers.ETag!.ToString();
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(tenant.Scim, HttpMethod.Get, $"/scim/v2/Users/{id}", ifNoneMatch: first)).StatusCode);

        var stale = await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Users/{id}", Patch(new { op = "replace", path = "displayName", value = "Lost" }), ifMatch: first);
        Assert.Equal("412", (await JsonAsync(stale, HttpStatusCode.PreconditionFailed)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await SendAsync(tenant.Scim, HttpMethod.Put, $"/scim/v2/Users/{id}", new { userName = "lost@example.com" }, ifMatch: first)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await SendAsync(tenant.Scim, HttpMethod.Delete, $"/scim/v2/Users/{id}", ifMatch: first)).StatusCode);
        Assert.Equal("Renamed", (await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Get, $"/scim/v2/Users/{id}"))).GetProperty("displayName").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(tenant.Scim, HttpMethod.Delete, $"/scim/v2/Users/{id}", ifMatch: $"\"x\", {second}")).StatusCode);
        Assert.False((await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Get, $"/scim/v2/Users/{id}"))).GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Lists_AreSortedPagedAndProjected()
    {
        var tenant = await CreateTenantAsync(AllScopes);
        var prefix = Guid.NewGuid().ToString("N")[..8];
        foreach (var name in new[] { "carla", "ana", "bruno" })
            await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Post, "/scim/v2/Users", new { userName = $"{name}-{prefix}@example.com", displayName = name }), HttpStatusCode.Created);

        var descending = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Get, "/scim/v2/Users?sortBy=userName&sortOrder=descending"));
        Assert.Equal(["carla", "bruno", "ana"], descending.GetProperty("Resources").EnumerateArray().Select(user => user.GetProperty("displayName").GetString()));

        var second = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Get, "/scim/v2/Users?sortBy=displayName&startIndex=2&count=1"));
        Assert.Equal(3, second.GetProperty("totalResults").GetInt32());
        Assert.Equal(2, second.GetProperty("startIndex").GetInt32());
        Assert.Equal(1, second.GetProperty("itemsPerPage").GetInt32());
        Assert.Equal("bruno", second.GetProperty("Resources")[0].GetProperty("displayName").GetString());

        var countOnly = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Get, "/scim/v2/Users?count=0"));
        Assert.Equal(3, countOnly.GetProperty("totalResults").GetInt32());
        Assert.Equal(0, countOnly.GetProperty("Resources").GetArrayLength());
        var clamped = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Get, "/scim/v2/Users?startIndex=0&count=500"));
        Assert.Equal(1, clamped.GetProperty("startIndex").GetInt32());
        Assert.Equal(3, clamped.GetProperty("itemsPerPage").GetInt32());

        var projected = (await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Get, $"/scim/v2/Users?attributes=userName&filter={Uri.EscapeDataString($"userName eq \"ana-{prefix}@example.com\"")}"))).GetProperty("Resources")[0];
        Assert.Equal(["schemas", "id", "userName"], projected.EnumerateObject().Select(property => property.Name));
        var excluded = (await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Get, "/scim/v2/Users?excludedAttributes=emails,meta"))).GetProperty("Resources")[0];
        Assert.False(excluded.TryGetProperty("emails", out _));
        Assert.False(excluded.TryGetProperty("meta", out _));
        Assert.True(excluded.TryGetProperty("userName", out _));

        var unsortable = await SendAsync(tenant.Scim, HttpMethod.Get, "/scim/v2/Users?sortBy=nickName");
        Assert.Equal("invalidValue", (await JsonAsync(unsortable, HttpStatusCode.BadRequest)).GetProperty("scimType").GetString());
    }

    [Fact]
    public async Task Groups_FollowTheMemberOperationsOfEntraIdAndOkta()
    {
        var tenant = await CreateTenantAsync(AllScopes);
        var users = new List<string>();
        for (var index = 0; index < 3; index++)
            users.Add((await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Post, "/scim/v2/Users", new { userName = $"member{index}-{Guid.NewGuid():N}@example.com" }), HttpStatusCode.Created)).GetProperty("id").GetString()!);
        var name = $"Engineering {Guid.NewGuid():N}";

        var created = await SendAsync(tenant.Scim, HttpMethod.Post, "/scim/v2/Groups", new
        {
            schemas = new[] { GroupSchema }, displayName = name, externalId = "g-1", members = new[] { new { value = users[0] }, new { value = users[1] } }
        });
        var group = await JsonAsync(created, HttpStatusCode.Created);
        var id = group.GetProperty("id").GetString();
        Assert.Equal(Sorted(users[0], users[1]), Members(group));
        Assert.EndsWith($"/scim/v2/Users/{group.GetProperty("members")[0].GetProperty("value").GetString()}", group.GetProperty("members")[0].GetProperty("$ref").GetString());

        // Entra ID removes with a list of values.
        var entra = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Groups/{id}", Patch(new { op = "Remove", path = "members", value = new[] { new { value = users[0] } } })));
        Assert.Equal([users[1]], Members(entra));
        // Okta replaces the whole list.
        var okta = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Groups/{id}", Patch(new { op = "replace", path = "members", value = new[] { new { value = users[0] }, new { value = users[2] } } })));
        Assert.Equal(Sorted(users[0], users[2]), Members(okta));
        var selected = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Groups/{id}", Patch(new { op = "remove", path = $"members[value eq \"{users[2]}\"]" })));
        Assert.Equal([users[0]], Members(selected));
        var renamed = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Groups/{id}", Patch(new { op = "replace", value = new { displayName = $"{name} Renamed" } })));
        Assert.Equal($"{name} Renamed", renamed.GetProperty("displayName").GetString());

        var replaced = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Put, $"/scim/v2/Groups/{id}", new { displayName = name, members = new[] { new { value = users[1] } } }));
        Assert.Equal([users[1]], Members(replaced));
        Assert.False(replaced.TryGetProperty("externalId", out _));
        var withoutMembers = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Get, $"/scim/v2/Groups/{id}?excludedAttributes=members"));
        Assert.False(withoutMembers.TryGetProperty("members", out _));

        var outsider = (await CreateTenantAsync(AllScopes)).Scim;
        var foreignId = (await JsonAsync(await SendAsync(outsider, HttpMethod.Post, "/scim/v2/Users", new { userName = $"foreign-{Guid.NewGuid():N}@example.com" }), HttpStatusCode.Created)).GetProperty("id").GetString();
        var foreign = await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Groups/{id}", Patch(new { op = "add", path = "members", value = new[] { new { value = foreignId } } }));
        Assert.Equal("invalidValue", (await JsonAsync(foreign, HttpStatusCode.BadRequest)).GetProperty("scimType").GetString());

        var otherName = $"Sales {Guid.NewGuid():N}";
        await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Post, "/scim/v2/Groups", new { displayName = otherName }), HttpStatusCode.Created);
        var duplicate = await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Groups/{id}", Patch(new { op = "replace", path = "displayName", value = otherName }));
        Assert.Equal("uniqueness", (await JsonAsync(duplicate, HttpStatusCode.Conflict)).GetProperty("scimType").GetString());
        var sorted = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Get, "/scim/v2/Groups?sortBy=displayName&sortOrder=descending&excludedAttributes=members"));
        Assert.Equal([otherName, name], sorted.GetProperty("Resources").EnumerateArray().Select(item => item.GetProperty("displayName").GetString()));
    }

    [Fact]
    public async Task MappedAttributes_FollowTheirScimPath_InEveryRequestShape()
    {
        var tenant = await CreateTenantAsync(AllScopes);
        var department = await CreateMappingAsync(tenant, "String", $"{EnterpriseSchema}:department");
        var division = await CreateMappingAsync(tenant, "String", $"{EnterpriseSchema}.division");
        var givenName = await CreateMappingAsync(tenant, "String", "name.givenName");
        var workPhone = await CreateMappingAsync(tenant, "String", "phoneNumbers[type eq \"work\"].value");

        var created = await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Post, "/scim/v2/Users", new Dictionary<string, object>
        {
            ["schemas"] = new[] { UserSchema, EnterpriseSchema },
            ["userName"] = $"grace-{Guid.NewGuid():N}@example.com",
            ["name"] = new { givenName = "Grace", familyName = "Hopper" },
            ["phoneNumbers"] = new[] { new { type = "home", value = "+1 555 0100" }, new { type = "work", value = "+1 555 0199" } },
            [EnterpriseSchema] = new { department = "Research", division = "Labs" }
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetString()!;
        var profile = await ProfileAsync(tenant.Admin, id);
        Assert.Equal("Research", profile[department.Key]);
        Assert.Equal("Labs", profile[division.Key]);
        Assert.Equal("Grace", profile[givenName.Key]);
        Assert.Equal("+1 555 0199", profile[workPhone.Key]);
        // The mapped extension attributes are part of the representation, and of its version.
        Assert.Contains(EnterpriseSchema, created.GetProperty("schemas").EnumerateArray().Select(schema => schema.GetString()));
        Assert.Equal("Research", created.GetProperty(EnterpriseSchema).GetProperty("department").GetString());
        Assert.Equal("Grace", created.GetProperty("name").GetProperty("givenName").GetString());

        await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Users/{id}", Patch(new { op = "replace", path = $"{EnterpriseSchema}:department", value = "Engineering" })));
        Assert.Equal("Engineering", (await ProfileAsync(tenant.Admin, id))[department.Key]);
        await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Users/{id}", Patch(new { op = "Replace", value = new Dictionary<string, object> { [$"{EnterpriseSchema}:division"] = "Platform" } })));
        Assert.Equal("Platform", (await ProfileAsync(tenant.Admin, id))[division.Key]);
        await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Users/{id}", Patch(new { op = "add", value = new Dictionary<string, object> { [EnterpriseSchema] = new { department = "Operations" } } })));
        Assert.Equal("Operations", (await ProfileAsync(tenant.Admin, id))[department.Key]);
        // Replacing a multi-valued attribute drops the work number it no longer has.
        await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Users/{id}", Patch(new { op = "replace", path = "phoneNumbers", value = new[] { new { type = "home", value = "+1 555 0100" } } })));
        Assert.False((await ProfileAsync(tenant.Admin, id)).ContainsKey(workPhone.Key));
        await JsonAsync(await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Users/{id}", Patch(new { op = "remove", path = $"{EnterpriseSchema}:department" })));
        Assert.False((await ProfileAsync(tenant.Admin, id)).ContainsKey(department.Key));

        var level = await CreateMappingAsync(tenant, "Integer", $"{EnterpriseSchema}:employeeNumber");
        var invalid = await SendAsync(tenant.Scim, HttpMethod.Patch, $"/scim/v2/Users/{id}", Patch(new { op = "replace", path = $"{EnterpriseSchema}:employeeNumber", value = "many" }));
        var error = await JsonAsync(invalid, HttpStatusCode.BadRequest);
        Assert.Equal("invalidValue", error.GetProperty("scimType").GetString());
        Assert.Contains(level.Key, error.GetProperty("detail").GetString());

        // The console's simulation reads paths exactly like SCIM requests, the earlier dotted form included.
        var simulated = await ReadDataAsync<ProfileMappingSimulationDto>(await tenant.Admin.PostAsJsonAsync($"/api/lifecycle/profile-mappings/{division.MappingId}/simulate", new ProfileMappingSimulationRequest
        {
            SourceDocument = JsonDocument.Parse($"{{\"{EnterpriseSchema}\":{{\"division\":\"Labs\"}}}}").RootElement.Clone()
        }));
        Assert.True(simulated.IsValid);
        Assert.Equal("Labs", simulated.Value?.GetString());
        var tooDeep = await tenant.Admin.PostAsJsonAsync("/api/lifecycle/profile-mappings/validate", new CreateProfileMappingRequest { ApplicationSystemId = tenant.ApplicationId, SourcePath = "a.b.c", TargetAttributeDefinitionId = department.DefinitionId });
        Assert.Equal("INVALID_PROFILE_MAPPING", (await tooDeep.Content.ReadFromJsonAsync<ApiResponse>())?.ErrorCode);
    }

    [Fact]
    public async Task Diagnostics_RecordTheRequestsOfEachToken_WithoutPayloads()
    {
        var tenant = await CreateTenantAsync("scim.users.read", "scim.users.write");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(tenant.Scim, HttpMethod.Get, "/scim/v2/Users")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(tenant.Scim, HttpMethod.Post, "/scim/v2/Users", new { displayName = "secret-display-name" })).StatusCode);
        var forbidden = await SendAsync(tenant.Scim, HttpMethod.Get, "/scim/v2/Groups");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains("insufficient_scope", forbidden.Headers.WwwAuthenticate.ToString());

        var diagnostics = await ReadDataAsync<ScimDiagnosticsDto>(await tenant.Admin.GetAsync($"/api/provisioning-tokens/{tenant.TokenId}/diagnostics"));
        Assert.Equal(3, diagnostics.Last24Hours.Total);
        Assert.Equal(2, diagnostics.Last24Hours.Failed);
        Assert.NotNull(diagnostics.LastSucceededAt);
        Assert.Contains(diagnostics.Failures, failure => failure.StatusCode == 403 && failure.LastDetail!.Contains("scim.groups.read"));
        Assert.Contains(diagnostics.Failures, failure => failure.StatusCode == 400 && failure.ScimType == "invalidValue");

        var failed = await ReadDataAsync<PagedResult<ScimRequestLogDto>>(await tenant.Admin.GetAsync($"/api/provisioning-tokens/{tenant.TokenId}/requests?outcome=failed&page=1&pageSize=10"));
        Assert.Equal(2, failed.TotalCount);
        var refused = Assert.Single(failed.Items, request => request.Method == "POST");
        Assert.Equal("/scim/v2/Users", refused.Path);
        Assert.False(string.IsNullOrEmpty(refused.TraceId));
        Assert.DoesNotContain(failed.Items, request => request.Detail?.Contains("secret-display-name") == true);

        await tenant.Admin.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.provisioning-token.revoke");
        Assert.Equal(HttpStatusCode.OK, (await tenant.Admin.DeleteAsync($"/api/provisioning-tokens/{tenant.TokenId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(tenant.Scim, HttpMethod.Get, "/scim/v2/Users")).StatusCode);
        var all = await ReadDataAsync<PagedResult<ScimRequestLogDto>>(await tenant.Admin.GetAsync($"/api/provisioning-tokens/{tenant.TokenId}/requests"));
        Assert.Equal(4, all.TotalCount);
        Assert.Equal("The provisioning token was revoked.", all.Items[0].Detail);

        using var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "acp_unknown");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/scim/v2/Users")).StatusCode);
    }

    private sealed record Tenant(HttpClient Admin, HttpClient Scim, Guid ApplicationId, Guid TokenId);

    private sealed record Mapping(Guid MappingId, Guid DefinitionId, string Key);

    private async Task<Tenant> CreateTenantAsync(params string[] scopes)
    {
        var admin = await CreateAdminClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var application = await ReadDataAsync<ApplicationDto>(await admin.PostAsJsonAsync("/api/applications", new CreateApplicationRequest
        {
            Code = $"SCIM{suffix}", Name = $"SCIM protocol {suffix}", RegistrationMode = "InviteOnly", AllowPasswordLogin = true
        }));
        var token = await ReadDataAsync<ProvisioningTokenResponse>(await admin.PostAsJsonAsync("/api/provisioning-tokens", new CreateProvisioningTokenRequest
        {
            ApplicationSystemId = application.Id, Name = "protocol", Scopes = scopes, ExpiresAt = DateTime.UtcNow.AddHours(1)
        }));
        var scim = _factory.CreateClient();
        scim.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return new Tenant(admin, scim, application.Id, token.Id);
    }

    private static async Task<Mapping> CreateMappingAsync(Tenant tenant, string dataType, string sourcePath)
    {
        var definition = await ReadDataAsync<ProfileAttributeDefinitionDto>(await tenant.Admin.PostAsJsonAsync("/api/profile-schema", new CreateProfileAttributeDefinitionRequest
        {
            Key = $"scim-{Guid.NewGuid():N}"[..24], DisplayName = "Mapped", DataType = dataType
        }));
        var mapping = await ReadDataAsync<ProfileMappingDto>(await tenant.Admin.PostAsJsonAsync("/api/lifecycle/profile-mappings", new CreateProfileMappingRequest
        {
            ApplicationSystemId = tenant.ApplicationId, SourcePath = sourcePath, TargetAttributeDefinitionId = definition.Id, IsAuthoritative = true
        }));
        return new Mapping(mapping.Id, definition.Id, definition.Key);
    }

    private static async Task<Dictionary<string, string?>> ProfileAsync(HttpClient admin, string userId)
    {
        var profile = await ReadDataAsync<UserProfileDto>(await admin.GetAsync($"/api/users/{userId}/profile"));
        return profile.Attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value.ValueKind == JsonValueKind.String ? attribute.Value.GetString() : attribute.Value.GetRawText());
    }

    private static object Patch(params object[] operations) => new { schemas = new[] { PatchSchema }, Operations = operations };

    private static List<string> Members(JsonElement group) =>
        [.. group.GetProperty("members").EnumerateArray().Select(member => member.GetProperty("value").GetString()!).Order(StringComparer.Ordinal)];

    private static List<string> Sorted(params string[] ids) => [.. ids.Order(StringComparer.Ordinal)];

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, string? ifMatch = null, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/scim+json");
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
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

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response) where T : class
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {(int)response.StatusCode} {body}");
        var result = JsonSerializer.Deserialize<ApiResponse<T>>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(result?.Data);
        return result.Data;
    }
}
