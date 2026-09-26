using AuthCenter.Api.Services;

namespace AuthCenter.Api.Extensions;

/// <summary>
/// Rate-limit policies. Each policy is a set of fixed-window rules, each counted per IP address,
/// per account (the email in the request body) or per OAuth client, so an attacker cannot avoid
/// a limit by rotating addresses and a busy server-side client is not throttled as one IP.
/// </summary>
public static class RateLimitingExtensions
{
    public const string Login = "auth-login";
    public const string Register = "auth-register";
    public const string Refresh = "auth-refresh";
    public const string ForgotPassword = "auth-forgot-password";
    public const string Google = "auth-google";
    public const string Microsoft = "auth-microsoft";
    public const string GitHub = "auth-github";
    public const string Apple = "auth-apple";
    public const string ResetPassword = "auth-reset-password";
    public const string ConfirmEmail = "auth-confirm-email";
    public const string ResendEmailConfirmation = "auth-resend-email-confirmation";
    public const string ChangePassword = "auth-change-password";
    public const string EmailChangeRequest = "auth-email-change-request";
    public const string MfaVerify = "auth-mfa-verify";
    public const string ForcedChangePassword = "auth-forced-change-password";
    public const string MagicLinkRequest = "auth-magic-link-request";
    public const string MagicLinkVerify = "auth-magic-link-verify";
    public const string SendMfaEmailOtp = "auth-send-mfa-email-otp";
    public const string MfaEmailOtpEnable = "auth-mfa-email-otp-enable";
    public const string OAuthToken = "oauth-token";
    public const string FederationDiscover = "federation-discover";
    public const string FederationStart = "federation-start";
    public const string FederationComplete = "federation-complete";
    public const string SamlIdentityProvider = "saml-idp";

    private static RateLimitRule PerIp(int permits, TimeSpan window) => new(RateLimitDimension.Ip, permits, window);
    private static RateLimitRule PerAccount(int permits, TimeSpan window) => new(RateLimitDimension.Account, permits, window);

    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<RateLimitRule>> DefaultRules =
        new Dictionary<string, IReadOnlyList<RateLimitRule>>(StringComparer.Ordinal)
        {
            // Per account the budget is generous: it stops distributed guessing without letting a
            // stranger lock a user out as easily as a tight limit would.
            [Login] = [PerIp(5, TimeSpan.FromMinutes(1)), PerAccount(20, TimeSpan.FromMinutes(15))],
            // SAML requests arrive as browser navigations from service providers: generous, but bounded per address.
            [SamlIdentityProvider] = [PerIp(60, TimeSpan.FromMinutes(1))],
            [Register] = [PerIp(3, TimeSpan.FromMinutes(1)), PerAccount(3, TimeSpan.FromHours(1))],
            [Refresh] = [PerIp(10, TimeSpan.FromMinutes(1))],
            // Per account these stop mail bombing a victim from many addresses.
            [ForgotPassword] = [PerIp(3, TimeSpan.FromMinutes(5)), PerAccount(3, TimeSpan.FromHours(1))],
            [Google] = [PerIp(5, TimeSpan.FromMinutes(1))],
            [Microsoft] = [PerIp(5, TimeSpan.FromMinutes(1))],
            [GitHub] = [PerIp(5, TimeSpan.FromMinutes(1))],
            [Apple] = [PerIp(5, TimeSpan.FromMinutes(1))],
            [ResetPassword] = [PerIp(3, TimeSpan.FromMinutes(5))],
            [ConfirmEmail] = [PerIp(5, TimeSpan.FromHours(1))],
            [ResendEmailConfirmation] = [PerIp(3, TimeSpan.FromMinutes(10)), PerAccount(3, TimeSpan.FromHours(1))],
            [ChangePassword] = [PerIp(5, TimeSpan.FromMinutes(1))],
            [EmailChangeRequest] = [PerIp(3, TimeSpan.FromHours(1))],
            [MfaVerify] = [PerIp(5, TimeSpan.FromMinutes(1))],
            [ForcedChangePassword] = [PerIp(5, TimeSpan.FromMinutes(1))],
            [MagicLinkRequest] = [PerIp(3, TimeSpan.FromMinutes(10)), PerAccount(5, TimeSpan.FromHours(1))],
            [MagicLinkVerify] = [PerIp(10, TimeSpan.FromMinutes(1))],
            [SendMfaEmailOtp] = [PerIp(3, TimeSpan.FromMinutes(5))],
            [MfaEmailOtpEnable] = [PerIp(5, TimeSpan.FromMinutes(1))],
            // Home realm discovery answers per email; the account budget limits probing one address.
            [FederationDiscover] = [PerIp(30, TimeSpan.FromMinutes(1)), PerAccount(10, TimeSpan.FromMinutes(1))],
            [FederationStart] = [PerIp(20, TimeSpan.FromMinutes(1))],
            [FederationComplete] = [PerIp(20, TimeSpan.FromMinutes(1))],
            // Server-side clients call from one address for all their users, so the token endpoints
            // count per client; only requests that name no client fall back to the address.
            [OAuthToken] =
            [
                new(RateLimitDimension.Client, 1200, TimeSpan.FromMinutes(1)),
                new(RateLimitDimension.AnonymousIp, 60, TimeSpan.FromMinutes(1))
            ]
        };

    /// <summary>
    /// Registers the rate limiter: a SQL-backed store shared by every instance when
    /// <c>RateLimiting:DistributedEnabled</c> is set, otherwise an in-memory store.
    /// </summary>
    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, bool distributed)
    {
        services.AddSingleton<RateLimitRules>();
        if (distributed)
            services.AddScoped<IRateLimitStore, DistributedRateLimitStore>();
        else
            services.AddSingleton<IRateLimitStore, InMemoryRateLimitStore>();
        return services;
    }
}
