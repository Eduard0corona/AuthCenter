using System.Globalization;
using System.Text.Json;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;

namespace AuthCenter.Infrastructure.Services;

/// <summary>
/// The conditions of dynamic group rules: a profile attribute compared with an expected value. Values
/// are the canonical JSON the profile stores (see <see cref="UserProfileService"/>).
/// </summary>
internal static class GroupRuleEvaluator
{
    public const string Equal = "eq";
    public const string NotEqual = "ne";
    public const string In = "in";
    public const string Contains = "contains";
    public const string StartsWith = "startsWith";
    public const string GreaterThan = "gt";
    public const string GreaterOrEqual = "gte";
    public const string LessThan = "lt";
    public const string LessOrEqual = "lte";
    public const string Exists = "exists";
    private const int MaxListValues = 100;

    public static readonly IReadOnlyList<string> Operators = [Equal, NotEqual, In, Contains, StartsWith, GreaterThan, GreaterOrEqual, LessThan, LessOrEqual, Exists];

    /// <summary>Whether the operator applies to attributes of the type (ordering needs numbers or dates).</summary>
    public static bool Supports(string op, ProfileAttributeDataType type) => op switch
    {
        Equal or NotEqual or In or Exists => true,
        Contains or StartsWith => type == ProfileAttributeDataType.String,
        GreaterThan or GreaterOrEqual or LessThan or LessOrEqual => type is ProfileAttributeDataType.Integer or ProfileAttributeDataType.Decimal or ProfileAttributeDataType.Date or ProfileAttributeDataType.DateTime,
        _ => false
    };

    /// <summary>
    /// The expected value in canonical JSON: a value of the attribute's type, a list of them for
    /// <c>in</c>, or <c>true</c> for <c>exists</c>.
    /// </summary>
    public static bool TryNormalizeExpected(string op, UserProfileAttributeDefinition definition, JsonElement expected, out string json, out string error)
    {
        json = string.Empty;
        error = string.Empty;
        if (!Supports(op, definition.DataType))
        {
            error = $"The {op} operator does not apply to {definition.DataType} attributes.";
            return false;
        }
        if (op == Exists)
        {
            if (expected.ValueKind != JsonValueKind.True) { error = "The exists operator expects true."; return false; }
            json = "true";
            return true;
        }
        if (op == In)
        {
            if (expected.ValueKind != JsonValueKind.Array || expected.GetArrayLength() is 0 or > MaxListValues)
            {
                error = $"The in operator expects a list of 1-{MaxListValues} values.";
                return false;
            }
            var items = new List<string>();
            foreach (var item in expected.EnumerateArray())
            {
                if (!TryNormalizeScalar(definition, item, out var itemJson, out error)) return false;
                items.Add(itemJson);
            }
            json = $"[{string.Join(',', items.Distinct(StringComparer.Ordinal))}]";
            return true;
        }
        if ((op is Contains or StartsWith) && (expected.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(expected.GetString())))
        {
            error = $"The {op} operator expects a non-empty text.";
            return false;
        }
        return TryNormalizeScalar(definition, expected, out json, out error);
    }

    /// <summary>Whether a stored value (null when the user has none) satisfies the rule.</summary>
    public static bool Matches(DynamicGroupRule rule, ProfileAttributeDataType type, string? actualJson)
    {
        if (actualJson is null) return false;
        if (rule.Operator == Exists) return true;
        using var actualDocument = JsonDocument.Parse(actualJson);
        using var expectedDocument = JsonDocument.Parse(rule.ExpectedValueJson);
        var actual = actualDocument.RootElement;
        var expected = expectedDocument.RootElement;
        return rule.Operator switch
        {
            Equal => Compare(type, actual, expected) == 0,
            NotEqual => Compare(type, actual, expected) != 0,
            In => expected.ValueKind == JsonValueKind.Array && expected.EnumerateArray().Any(item => Compare(type, actual, item) == 0),
            Contains => actual.ValueKind == JsonValueKind.String && actual.GetString()!.Contains(expected.GetString()!, StringComparison.OrdinalIgnoreCase),
            StartsWith => actual.ValueKind == JsonValueKind.String && actual.GetString()!.StartsWith(expected.GetString()!, StringComparison.OrdinalIgnoreCase),
            GreaterThan => Compare(type, actual, expected) > 0,
            GreaterOrEqual => Compare(type, actual, expected) >= 0,
            LessThan => Compare(type, actual, expected) < 0,
            LessOrEqual => Compare(type, actual, expected) <= 0,
            _ => false
        };
    }

    private static bool TryNormalizeScalar(UserProfileAttributeDefinition definition, JsonElement value, out string json, out string error)
    {
        // The same canonical form profile values are stored in, without the attribute's own constraints.
        var result = UserProfileService.NormalizeValue(new UserProfileAttributeDefinition { DataType = definition.DataType }, value, checkAllowedValues: false);
        json = result.ValueJson ?? string.Empty;
        error = result.IsSuccess ? string.Empty : $"The expected value is not a valid {definition.DataType}: {result.Message}";
        return result.IsSuccess;
    }

    /// <summary>Numbers by value, dates in time; text ordinally (an unordered comparison of other kinds).</summary>
    private static int Compare(ProfileAttributeDataType type, JsonElement actual, JsonElement expected)
    {
        switch (type)
        {
            case ProfileAttributeDataType.Integer or ProfileAttributeDataType.Decimal
                when actual.ValueKind == JsonValueKind.Number && expected.ValueKind == JsonValueKind.Number &&
                     actual.TryGetDecimal(out var actualNumber) && expected.TryGetDecimal(out var expectedNumber):
                return actualNumber.CompareTo(expectedNumber);
            case ProfileAttributeDataType.DateTime
                when actual.ValueKind == JsonValueKind.String && expected.ValueKind == JsonValueKind.String &&
                     DateTimeOffset.TryParse(actual.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var actualTime) &&
                     DateTimeOffset.TryParse(expected.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expectedTime):
                return actualTime.CompareTo(expectedTime);
            case ProfileAttributeDataType.Date or ProfileAttributeDataType.String when actual.ValueKind == JsonValueKind.String && expected.ValueKind == JsonValueKind.String:
                // Canonical dates are yyyy-MM-dd, so text order is date order.
                return string.CompareOrdinal(actual.GetString(), expected.GetString());
            default:
                return string.Equals(actual.GetRawText(), expected.GetRawText(), StringComparison.Ordinal) ? 0 : 1;
        }
    }
}
