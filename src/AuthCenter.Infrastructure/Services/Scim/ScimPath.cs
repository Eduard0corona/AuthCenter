using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuthCenter.Infrastructure.Services.Scim;

/// <summary>
/// A SCIM attribute path (RFC 7644 §3.10): <c>attr</c>, <c>attr.sub</c>, an extension attribute
/// <c>urn:…:User:attr</c> and a value filter <c>attr[sub eq "x"].sub</c>. Attribute names are
/// case-insensitive; core attributes may be written with their schema URN. The dotted extension form
/// earlier mappings used (<c>urn:…:2.0:User.department</c>) is read as well.
/// </summary>
public sealed partial record ScimPath(string? Schema, string Attribute, string? SubAttribute, ScimValueFilter? Filter)
{
    public const string CoreUserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string CoreGroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";
    public const string EnterpriseUserSchema = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";
    private static readonly string[] KnownSchemas = [EnterpriseUserSchema, CoreUserSchema, CoreGroupSchema];

    /// <summary>The path in its canonical spelling: core attributes without their schema.</summary>
    public string Canonical => (Schema is null ? string.Empty : Schema + ":") + Attribute +
        (Filter is null ? string.Empty : $"[{Filter.Attribute} eq {Filter.Value.GetRawText()}]") +
        (SubAttribute is null ? string.Empty : "." + SubAttribute);

    public static bool TryParse(string? text, out ScimPath path)
    {
        path = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 300)
            return false;
        text = text.Trim();
        // Earlier mappings could be written as JSONPath ($.department).
        if (text.StartsWith("$.", StringComparison.Ordinal))
            text = text[2..];
        string? schema = null;
        var rest = text;
        if (text.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
        {
            var known = KnownSchemas.FirstOrDefault(candidate => text.Length > candidate.Length + 1 &&
                text.StartsWith(candidate, StringComparison.OrdinalIgnoreCase) && text[candidate.Length] is ':' or '.');
            if (known is not null)
            {
                schema = known;
                rest = text[(known.Length + 1)..];
            }
            else
            {
                // Another extension: its URN ends at the last colon before the attribute.
                var head = text.Split('[')[0];
                var colon = head.LastIndexOf(':');
                if (colon <= 4 || colon == head.Length - 1)
                    return false;
                schema = text[..colon];
                rest = text[(colon + 1)..];
            }
            if (string.Equals(schema, CoreUserSchema, StringComparison.OrdinalIgnoreCase) || string.Equals(schema, CoreGroupSchema, StringComparison.OrdinalIgnoreCase))
                schema = null;
        }

        var match = AttributePattern().Match(rest);
        if (!match.Success)
            return false;
        ScimValueFilter? filter = null;
        if (match.Groups["filterAttribute"].Success)
        {
            try
            {
                using var value = JsonDocument.Parse(match.Groups["filterValue"].Value);
                if (value.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    return false;
                filter = new ScimValueFilter(match.Groups["filterAttribute"].Value, value.RootElement.Clone());
            }
            catch (JsonException)
            {
                return false;
            }
        }
        path = new ScimPath(schema, match.Groups["attribute"].Value, match.Groups["sub"].Success ? match.Groups["sub"].Value : null, filter);
        return true;
    }

    public bool IsEquivalentTo(ScimPath other) =>
        string.Equals(Canonical, other.Canonical, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The value this path selects in a resource document. A multi-valued attribute without a filter
    /// gives its primary value (or its first).
    /// </summary>
    public bool TryResolve(JsonElement resource, out JsonElement value)
    {
        value = default;
        if (resource.ValueKind != JsonValueKind.Object)
            return false;
        var container = resource;
        if (Schema is not null && !(TryProperty(resource, Schema, out container) && container.ValueKind == JsonValueKind.Object))
            return false;
        if (!TryProperty(container, Attribute, out var attribute))
            return false;
        if (attribute.ValueKind == JsonValueKind.Array)
        {
            var items = attribute.EnumerateArray().ToList();
            var selected = Filter is null
                ? items.FirstOrDefault(item => item.ValueKind == JsonValueKind.Object && TryProperty(item, "primary", out var primary) && primary.ValueKind == JsonValueKind.True)
                : items.FirstOrDefault(item => item.ValueKind == JsonValueKind.Object && TryProperty(item, Filter.Attribute, out var candidate) && Filter.Matches(candidate));
            if (selected.ValueKind == JsonValueKind.Undefined)
            {
                if (Filter is not null || items.Count == 0)
                    return false;
                selected = items[0];
            }
            attribute = selected;
        }
        else if (Filter is not null)
        {
            return false;
        }
        if (SubAttribute is null)
        {
            value = attribute;
            return true;
        }
        return attribute.ValueKind == JsonValueKind.Object && TryProperty(attribute, SubAttribute, out value);
    }

    public static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value))
                return true;
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        }
        value = default;
        return false;
    }

    [GeneratedRegex("""^(?<attribute>[A-Za-z$][\w$-]*)(\[\s*(?<filterAttribute>[A-Za-z][\w$-]*)\s+eq\s+(?<filterValue>"(?:[^"\\]|\\.)*"|true|false|null|-?\d+(?:\.\d+)?)\s*\])?(\.(?<sub>[A-Za-z$][\w$-]*))?$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex AttributePattern();
}

/// <summary>The <c>[attr eq value]</c> selector of a multi-valued attribute; text compares without case.</summary>
public sealed record ScimValueFilter(string Attribute, JsonElement Value)
{
    public bool SameAs(ScimValueFilter other) =>
        string.Equals(Attribute, other.Attribute, StringComparison.OrdinalIgnoreCase) && Matches(other.Value);

    public bool Matches(JsonElement candidate) =>
        Value.ValueKind == JsonValueKind.String && candidate.ValueKind == JsonValueKind.String
            ? string.Equals(candidate.GetString(), Value.GetString(), StringComparison.OrdinalIgnoreCase)
            : string.Equals(candidate.GetRawText(), Value.GetRawText(), StringComparison.Ordinal);
}
