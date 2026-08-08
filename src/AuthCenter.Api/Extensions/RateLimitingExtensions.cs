using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthCenter.Api.Extensions;

public static class RateLimitingExtensions
{
    public readonly record struct PolicyLimit(int PermitLimit, TimeSpan Window);

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

    private static readonly IReadOnlyDictionary<string, PolicyLimit> PolicyLimits =
        new Dictionary<string, PolicyLimit>(StringComparer.Ordinal)
        {
            [Login] = new(5, TimeSpan.FromMinutes(1)),
            [Register] = new(3, TimeSpan.FromMinutes(1)),
            [Refresh] = new(10, TimeSpan.FromMinutes(1)),
            [ForgotPassword] = new(3, TimeSpan.FromMinutes(5)),
            [Google] = new(5, TimeSpan.FromMinutes(1)),
            [Microsoft] = new(5, TimeSpan.FromMinutes(1)),
            [GitHub] = new(5, TimeSpan.FromMinutes(1)),
            [Apple] = new(5, TimeSpan.FromMinutes(1)),
            [ResetPassword] = new(3, TimeSpan.FromMinutes(5)),
            [ConfirmEmail] = new(5, TimeSpan.FromHours(1)),
            [ResendEmailConfirmation] = new(3, TimeSpan.FromMinutes(10)),
            [ChangePassword] = new(5, TimeSpan.FromMinutes(1)),
            [EmailChangeRequest] = new(3, TimeSpan.FromHours(1)),
            [MfaVerify] = new(5, TimeSpan.FromMinutes(1)),
            [ForcedChangePassword] = new(5, TimeSpan.FromMinutes(1)),
            [MagicLinkRequest] = new(3, TimeSpan.FromMinutes(10)),
            [MagicLinkVerify] = new(10, TimeSpan.FromMinutes(1)),
            [SendMfaEmailOtp] = new(3, TimeSpan.FromMinutes(5)),
            [MfaEmailOtpEnable] = new(5, TimeSpan.FromMinutes(1)),
            [OAuthToken] = new(60, TimeSpan.FromMinutes(1))
        };

    public static bool TryGetPolicy(string name, out PolicyLimit policy) =>
        PolicyLimits.TryGetValue(name, out policy);

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // 5 intentos de login por IP por minuto
            options.AddPolicy(Login, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // 3 registros por IP por minuto
            options.AddPolicy(Register, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // 10 refreshes por IP por minuto
            options.AddPolicy(Refresh, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // 3 solicitudes de forgot-password por IP cada 5 minutos
            options.AddPolicy(ForgotPassword, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromMinutes(5),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // 5 intentos de Google login por IP por minuto
            options.AddPolicy(Google, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            options.AddPolicy(Microsoft, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            options.AddPolicy(GitHub, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            options.AddPolicy(Apple, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            options.AddPolicy(MfaVerify, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            options.AddPolicy(ForcedChangePassword, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            options.AddPolicy(MagicLinkRequest, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromMinutes(10),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            options.AddPolicy(MagicLinkVerify, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            options.AddPolicy(SendMfaEmailOtp, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromMinutes(5),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            options.AddPolicy(MfaEmailOtpEnable, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // 3 solicitudes de reset-password por IP cada 5 minutos
            options.AddPolicy(ResetPassword, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromMinutes(5),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // 5 solicitudes de confirm-email por IP por hora
            options.AddPolicy(ConfirmEmail, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromHours(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // 3 reenvíos de confirmación de email por IP cada 10 minutos
            options.AddPolicy(ResendEmailConfirmation, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromMinutes(10),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // 5 cambios de contraseña por IP por minuto
            options.AddPolicy(ChangePassword, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // 3 solicitudes de cambio de email por IP por hora
            options.AddPolicy(EmailChangeRequest, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromHours(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // The token endpoint validates client secrets and refresh tokens, so it needs a limit
            // for the same reason the login endpoint does. Kept looser than the interactive ones
            // because a single machine client legitimately exchanges tokens in bursts.
            options.AddPolicy(OAuthToken, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            options.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                ctx.HttpContext.Response.ContentType = "application/json";
                await ctx.HttpContext.Response.WriteAsync(
                    """{"success":false,"errorCode":"RATE_LIMIT_EXCEEDED","message":"Too many requests. Please try again later."}""",
                    ct);
            };
        });

        return services;
    }
}
