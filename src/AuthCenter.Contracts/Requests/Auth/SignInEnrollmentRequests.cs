namespace AuthCenter.Contracts.Requests.Auth;

/// <summary>Enrolls the authenticator app during a sign-in that requires MFA.</summary>
public sealed class TotpEnrollmentRequest
{
    public string EnrollmentToken { get; init; } = string.Empty;
    public string? TotpCode { get; init; }
}

/// <summary>Registers a passkey during a sign-in that requires one.</summary>
public sealed class PasskeyEnrollmentRequest
{
    public string EnrollmentToken { get; init; } = string.Empty;
    public string? CredentialJson { get; init; }
    public string? Name { get; init; }
}

/// <summary>
/// Redeems an emailed sign-in link from the hosted <c>/magic-link</c> page. The link does not name
/// the application: it is the one the signed token was issued for.
/// </summary>
public sealed class HostedMagicLinkRequest
{
    public string Token { get; init; } = string.Empty;
}
