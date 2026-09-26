namespace AuthCenter.Infrastructure.Services;

/// <summary>
/// Purposes used against <see cref="AuthCenter.Application.Interfaces.ITransientStateStore"/> for
/// the email OTP codes, which are written by one request and read by another.
/// </summary>
public static class MfaStatePurposes
{
    /// <summary>Code sent while enrolling in email OTP. Keyed by user id.</summary>
    public const string EmailOtpSetup = "emailotp_setup";

    /// <summary>Code sent to complete a pending sign-in. Keyed by the pending token's jti.</summary>
    public const string EmailOtpVerify = "emailotp_verify";

    /// <summary>A TOTP time step already accepted for a user. Keyed by user id and step.</summary>
    public const string TotpStepUsed = "totp_step_used";
}
