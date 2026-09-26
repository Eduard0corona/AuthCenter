namespace AuthCenter.Api.Extensions;

/// <summary>
/// Sign-in enrollment failures carry a single-use enrollment token in their message for the
/// hosted login; JSON API clients get a fixed explanation instead.
/// </summary>
internal static class SignInMessages
{
    public static string ForApi(string? code, string message) => code switch
    {
        "MFA_SETUP_REQUIRED" => "This application requires MFA. Set up two-factor authentication and sign in again.",
        "PASSKEY_ENROLLMENT_REQUIRED" => "This application requires a passkey. Register one and sign in again.",
        _ => message
    };
}
