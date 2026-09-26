using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuthCenter.Infrastructure.Services.Scim;

public static class ScimJson
{
    /// <summary>
    /// The resource's version (weak ETag): a hash of its representation without <c>meta</c>, so any
    /// change a client can see gives a new version.
    /// </summary>
    public static string Version(JsonObject body)
    {
        var copy = (JsonObject)body.DeepClone();
        copy.Remove("meta");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(copy.ToJsonString()));
        return $"W/\"{Convert.ToHexString(hash, 0, 12).ToLowerInvariant()}\"";
    }

    /// <summary>Whether an <c>If-Match</c> (absent, <c>*</c> or a list of tags, weak or strong) admits the version.</summary>
    public static bool IfMatchAllows(string? ifMatch, string version)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
            return true;
        var current = Opaque(version);
        return ifMatch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(tag => tag == "*" || string.Equals(Opaque(tag), current, StringComparison.Ordinal));
    }

    /// <summary>Whether an <c>If-None-Match</c> names the version (a GET can then answer 304).</summary>
    public static bool IfNoneMatchNames(string? ifNoneMatch, string version) =>
        !string.IsNullOrWhiteSpace(ifNoneMatch) && IfMatchAllows(ifNoneMatch, version);

    public static string Timestamp(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Applies <c>attributes</c> or <c>excludedAttributes</c> (RFC 7644 §3.4.2.5) to a resource: <c>id</c>
    /// and <c>schemas</c> are always returned; <c>meta</c> only when not excluded.
    /// </summary>
    public static JsonObject Project(JsonObject resource, string? attributes, string? excludedAttributes)
    {
        var requested = Paths(attributes);
        var excluded = Paths(excludedAttributes);
        if (requested.Count == 0 && excluded.Count == 0)
            return resource;
        var result = new JsonObject();
        foreach (var (name, value) in resource)
        {
            if (name is "id" or "schemas")
            {
                result[name] = value?.DeepClone();
                continue;
            }
            if (requested.Count > 0)
            {
                var wanted = requested.Where(path => string.Equals(path.Top, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (wanted.Count == 0)
                    continue;
                result[name] = wanted.Any(path => path.Sub is null) || value is not JsonObject complex
                    ? value?.DeepClone()
                    : Keep(complex, wanted.Select(path => path.Sub!));
                continue;
            }
            var dropped = excluded.Where(path => string.Equals(path.Top, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (dropped.Any(path => path.Sub is null))
                continue;
            result[name] = dropped.Count > 0 && value is JsonObject partial ? Drop(partial, dropped.Select(path => path.Sub!)) : value?.DeepClone();
        }
        return result;
    }

    /// <summary>Places a value at a path of a representation (an extension attribute or a sub-attribute).</summary>
    public static void Set(JsonObject body, ScimPath path, JsonNode? value)
    {
        var container = body;
        if (path.Schema is not null)
        {
            if (body[path.Schema] is not JsonObject extension)
                body[path.Schema] = extension = new JsonObject();
            container = extension;
            if (body["schemas"] is JsonArray schemas && !schemas.Any(schema => string.Equals(schema?.GetValue<string>(), path.Schema, StringComparison.OrdinalIgnoreCase)))
                schemas.Add(path.Schema);
        }
        if (path.SubAttribute is null)
        {
            container[path.Attribute] = value;
            return;
        }
        if (container[path.Attribute] is not JsonObject parent)
            container[path.Attribute] = parent = new JsonObject();
        parent[path.SubAttribute] = value;
    }

    public static JsonNode? Node(string json) => JsonNode.Parse(json);

    public static JsonNode? Node(JsonElement element) => JsonNode.Parse(element.GetRawText());

    private static List<(string Top, string? Sub)> Paths(string? list)
    {
        var paths = new List<(string, string?)>();
        if (string.IsNullOrWhiteSpace(list))
            return paths;
        foreach (var item in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ScimPath.TryParse(item, out var path))
                continue;
            // An extension's attributes live under its URN.
            paths.Add(path.Schema is null ? (path.Attribute, path.SubAttribute) : (path.Schema, path.Attribute));
        }
        return paths;
    }

    private static JsonObject Keep(JsonObject complex, IEnumerable<string> names)
    {
        var kept = new JsonObject();
        foreach (var name in names)
            foreach (var (key, value) in complex)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && !kept.ContainsKey(key))
                    kept[key] = value?.DeepClone();
        return kept;
    }

    private static JsonObject Drop(JsonObject complex, IEnumerable<string> names)
    {
        var copy = (JsonObject)complex.DeepClone();
        foreach (var name in names)
            foreach (var key in copy.Select(pair => pair.Key).Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)).ToList())
                copy.Remove(key);
        return copy;
    }

    private static string Opaque(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        return value.Trim('"');
    }
}
