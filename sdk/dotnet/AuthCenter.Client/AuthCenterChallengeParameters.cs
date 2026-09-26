using System.Globalization;
using System.Text.RegularExpressions;

namespace AuthCenter.Client;

/// <summary>
/// Validates the optional OpenID Connect request parameters a BFF forwards from its login
/// endpoint, and the protocol errors it hands back to the application after a failed sign-in.
/// AuthCenter validates every parameter again; this only keeps malformed input out of the request.
/// </summary>
internal static partial class AuthCenterChallengeParameters
{
    private const int MaxLoginHintLength = 256;
    private static readonly string[] PromptValues = ["none", "login", "consent", "select_account"];

    /// <summary>Errors an application can act on, for example by showing its own sign-in button.</summary>
    private static readonly HashSet<string> ForwardedErrors = new(StringComparer.Ordinal)
    {
        "login_required", "consent_required", "interaction_required", "account_selection_required", "access_denied"
    };

    public static string? Prompt(string? value)
    {
        var values = Split(value);
        if (values.Length == 0 || values.Any(item => !PromptValues.Contains(item, StringComparer.Ordinal)))
            return null;
        return values.Contains("none", StringComparer.Ordinal) && values.Length > 1 ? null : string.Join(' ', values);
    }

    public static TimeSpan? MaxAge(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? TimeSpan.FromSeconds(seconds) : null;

    public static string? LoginHint(string? value)
    {
        var hint = value?.Trim();
        return string.IsNullOrEmpty(hint) || hint.Length > MaxLoginHintLength || hint.Any(char.IsControl) ? null : hint;
    }

    public static string? AcrValues(string? value)
    {
        var values = Split(value);
        return values.Length == 0 || values.Any(item => !AcrValuePattern().IsMatch(item)) ? null : string.Join(' ', values);
    }

    public static string? ForwardedError(string? value) =>
        value is not null && ForwardedErrors.Contains(value) ? value : null;

    public static string? ForwardedError(Exception? failure) =>
        ForwardedError(failure?.Data["error"] as string);

    private static string[] Split(string? value) =>
        (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex("^[A-Za-z0-9:._/-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex AcrValuePattern();
}
