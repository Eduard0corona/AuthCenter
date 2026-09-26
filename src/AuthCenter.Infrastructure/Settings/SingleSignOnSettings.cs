namespace AuthCenter.Infrastructure.Settings;

/// <summary>
/// The hosted-login single sign-on session shared by every application. It is independent of
/// the access-token lifetime; entitlement changes and sign-out revoke it immediately.
/// </summary>
public class SingleSignOnSettings
{
    public int SessionLifetimeMinutes { get; init; } = 480;
}
