using System.Text.Json.Nodes;

namespace AuthCenter.Infrastructure.Services.Scim;

/// <summary>
/// The discovery documents of RFC 7643 §5-7: what the service supports, its resource types and the
/// schemas of their attributes. <paramref name="baseUrl"/> is the absolute <c>…/scim/v2</c> URL.
/// </summary>
public static class ScimDiscovery
{
    public const string ServiceProviderConfigSchema = "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig";
    public const string ResourceTypeSchema = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";
    public const string SchemaSchema = "urn:ietf:params:scim:schemas:core:2.0:Schema";
    public const string ListResponseSchema = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const int MaxResults = 200;

    public static JsonObject ServiceProviderConfig(string baseUrl) => new()
    {
        ["schemas"] = new JsonArray(ServiceProviderConfigSchema),
        ["patch"] = new JsonObject { ["supported"] = true },
        ["bulk"] = new JsonObject { ["supported"] = false, ["maxOperations"] = 0, ["maxPayloadSize"] = 0 },
        ["filter"] = new JsonObject { ["supported"] = true, ["maxResults"] = MaxResults },
        ["changePassword"] = new JsonObject { ["supported"] = false },
        ["sort"] = new JsonObject { ["supported"] = true },
        ["etag"] = new JsonObject { ["supported"] = true },
        ["authenticationSchemes"] = new JsonArray(new JsonObject
        {
            ["type"] = "oauthbearertoken",
            ["name"] = "Provisioning token",
            ["description"] = "An AuthCenter provisioning token (acp_…) bound to one application, sent as a bearer token. Its scopes grant read or write access to Users and Groups.",
            ["primary"] = true
        }),
        ["meta"] = Meta("ServiceProviderConfig", $"{baseUrl}/ServiceProviderConfig")
    };

    public static IReadOnlyList<JsonObject> ResourceTypes(string baseUrl) => [ResourceType("User", baseUrl)!, ResourceType("Group", baseUrl)!];

    public static JsonObject? ResourceType(string id, string baseUrl) => id.ToLowerInvariant() switch
    {
        "user" => new JsonObject
        {
            ["schemas"] = new JsonArray(ResourceTypeSchema),
            ["id"] = "User",
            ["name"] = "User",
            ["endpoint"] = "/Users",
            ["description"] = "A person with access to the application.",
            ["schema"] = ScimPath.CoreUserSchema,
            ["schemaExtensions"] = new JsonArray(new JsonObject { ["schema"] = ScimPath.EnterpriseUserSchema, ["required"] = false }),
            ["meta"] = Meta("ResourceType", $"{baseUrl}/ResourceTypes/User")
        },
        "group" => new JsonObject
        {
            ["schemas"] = new JsonArray(ResourceTypeSchema),
            ["id"] = "Group",
            ["name"] = "Group",
            ["endpoint"] = "/Groups",
            ["description"] = "A directory group assigned to the application.",
            ["schema"] = ScimPath.CoreGroupSchema,
            ["meta"] = Meta("ResourceType", $"{baseUrl}/ResourceTypes/Group")
        },
        _ => null
    };

    public static IReadOnlyList<JsonObject> Schemas(string baseUrl) =>
        [Schema(ScimPath.CoreUserSchema, baseUrl)!, Schema(ScimPath.CoreGroupSchema, baseUrl)!, Schema(ScimPath.EnterpriseUserSchema, baseUrl)!];

    public static JsonObject? Schema(string id, string baseUrl)
    {
        if (string.Equals(id, ScimPath.CoreUserSchema, StringComparison.OrdinalIgnoreCase))
            return SchemaDocument(ScimPath.CoreUserSchema, "User", "User Account", baseUrl,
                Attribute("userName", "string", "The user's email address, unique across the directory.", required: true, uniqueness: "server"),
                Complex("name", "The user's name. The formatted name is kept; givenName and familyName compose it when it is missing.",
                [
                    Attribute("formatted", "string", "The full name."),
                    Attribute("givenName", "string", "The given name."),
                    Attribute("familyName", "string", "The family name.")
                ]),
                Attribute("displayName", "string", "The name shown for the user."),
                Complex("emails", "The user's email address (the userName, read-only here).",
                [
                    Attribute("value", "string", "The address.", mutability: "readOnly"),
                    Attribute("type", "string", "Always work.", mutability: "readOnly"),
                    Attribute("primary", "boolean", "Always true.", mutability: "readOnly")
                ], multiValued: true, mutability: "readOnly"),
                Attribute("active", "boolean", "Whether the user can sign in to the application. DELETE deactivates the user."),
                Attribute("externalId", "string", "The provisioning client's identifier for the user.", caseExact: true));
        if (string.Equals(id, ScimPath.CoreGroupSchema, StringComparison.OrdinalIgnoreCase))
            return SchemaDocument(ScimPath.CoreGroupSchema, "Group", "Group", baseUrl,
                Attribute("displayName", "string", "The group's name, unique across the directory.", required: true, uniqueness: "server"),
                Complex("members", "The users of the application in the group. Groups whose members follow AuthCenter group rules refuse member changes.",
                [
                    Attribute("value", "string", "The user's id.", mutability: "immutable", caseExact: true),
                    Attribute("$ref", "reference", "The user's URI.", mutability: "immutable"),
                    Attribute("display", "string", "The user's name.", mutability: "readOnly"),
                    Attribute("type", "string", "Always User.", mutability: "immutable")
                ], multiValued: true),
                Attribute("externalId", "string", "The provisioning client's identifier for the group.", caseExact: true));
        if (string.Equals(id, ScimPath.EnterpriseUserSchema, StringComparison.OrdinalIgnoreCase))
            return SchemaDocument(ScimPath.EnterpriseUserSchema, "EnterpriseUser", "Enterprise User. An attribute is kept when a profile mapping of the application reads it; the others are ignored.", baseUrl,
                Attribute("employeeNumber", "string", "Employee number."),
                Attribute("costCenter", "string", "Cost center."),
                Attribute("organization", "string", "Organization."),
                Attribute("division", "string", "Division."),
                Attribute("department", "string", "Department."),
                Complex("manager", "The user's manager.",
                [
                    Attribute("value", "string", "The manager's id."),
                    Attribute("displayName", "string", "The manager's name.", mutability: "readOnly")
                ]));
        return null;
    }

    private static JsonObject SchemaDocument(string id, string name, string description, string baseUrl, params JsonObject[] attributes) => new()
    {
        ["schemas"] = new JsonArray(SchemaSchema),
        ["id"] = id,
        ["name"] = name,
        ["description"] = description,
        ["attributes"] = new JsonArray(attributes.Select(attribute => (JsonNode)attribute).ToArray()),
        ["meta"] = Meta("Schema", $"{baseUrl}/Schemas/{id}")
    };

    private static JsonObject Attribute(string name, string type, string description, bool required = false, bool caseExact = false,
        string mutability = "readWrite", string uniqueness = "none") => new()
    {
        ["name"] = name,
        ["type"] = type,
        ["multiValued"] = false,
        ["description"] = description,
        ["required"] = required,
        ["caseExact"] = caseExact,
        ["mutability"] = mutability,
        ["returned"] = "default",
        ["uniqueness"] = uniqueness
    };

    private static JsonObject Complex(string name, string description, JsonObject[] subAttributes, bool multiValued = false, string mutability = "readWrite")
    {
        var attribute = Attribute(name, "complex", description, mutability: mutability);
        attribute["multiValued"] = multiValued;
        attribute["subAttributes"] = new JsonArray(subAttributes.Select(sub => (JsonNode)sub).ToArray());
        return attribute;
    }

    private static JsonObject Meta(string resourceType, string location) => new() { ["resourceType"] = resourceType, ["location"] = location };
}
