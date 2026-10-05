using System.Text.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace AuthCenter.Infrastructure.Services;

public sealed class OutboxEmailService : IEmailService
{
    internal const string MessageType = "email.v1";
    private readonly AuthCenterDbContext _db;
    private readonly IDataProtector _protector;

    public OutboxEmailService(AuthCenterDbContext db, IDataProtectionProvider protectionProvider)
    {
        _db = db;
        _protector = protectionProvider.CreateProtector("AuthCenter.Outbox.Email.v1");
    }

    public Task SendPasswordResetAsync(string toEmail, string toName, string resetToken, string? callbackBaseUrl, CancellationToken ct = default) =>
        EnqueueAsync(new EmailPayload("password-reset", toEmail, toName, resetToken, callbackBaseUrl, null), ApplicationOf(callbackBaseUrl), ct);

    public Task SendEmailConfirmationAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default) =>
        EnqueueAsync(new EmailPayload("email-confirmation", toEmail, toName, token, callbackBaseUrl, null), ApplicationOf(callbackBaseUrl), ct);

    public async Task SendInvitationAsync(string toEmail, string toName, string applicationName, string token, string? callbackBaseUrl, CancellationToken ct = default)
    {
        var brand = await EmailBranding.ForCodeAsync(_db, ApplicationOf(callbackBaseUrl), ct);
        // An application that must not be named is not named by its invitation either.
        await EnqueueBrandedAsync(new EmailPayload("invitation", toEmail, toName, token, callbackBaseUrl, brand is { Name: null } ? null : applicationName), brand, ct);
    }

    public Task SendEmailChangeConfirmationAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default) =>
        EnqueueAsync(new EmailPayload("email-change", toEmail, toName, token, callbackBaseUrl, null), ApplicationOf(callbackBaseUrl), ct);

    public Task SendMagicLinkAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default) =>
        EnqueueAsync(new EmailPayload("magic-link", toEmail, toName, token, callbackBaseUrl, null), ApplicationOf(callbackBaseUrl), ct);

    public Task SendMfaEmailOtpAsync(string toEmail, string toName, string code, CancellationToken ct = default, int? validMinutes = null, string? applicationCode = null) =>
        EnqueueAsync(new EmailPayload("mfa-otp", toEmail, toName, code, null, null, ValidMinutes: validMinutes), applicationCode, ct);

    // The subject travels in ApplicationName and the detail in Secret, keeping the payload shape stable.
    public Task SendSecurityNoticeAsync(string toEmail, string toName, string subject, string detail, CancellationToken ct = default, string? applicationCode = null) =>
        EnqueueAsync(new EmailPayload("security-notice", toEmail, toName, detail, null, subject), applicationCode, ct);

    public Task SendNotificationAsync(string toEmail, string toName, string subject, string detail, string actionUrl, string actionLabel, CancellationToken ct = default) =>
        EnqueueBrandedAsync(new EmailPayload("notification", toEmail, toName, detail, actionUrl, subject, actionLabel), null, ct);

    /// <summary>The application an emailed link opens, which its <c>application</c> parameter names.</summary>
    private static string? ApplicationOf(string? callbackBaseUrl) =>
        Uri.TryCreate(callbackBaseUrl, UriKind.Absolute, out var url) &&
        QueryHelpers.ParseQuery(url.Query).TryGetValue("application", out var code) && code.Count > 0
            ? code[0]
            : null;

    // The brand is resolved now, so the email names the application as it was when it was asked for.
    private async Task EnqueueAsync(EmailPayload payload, string? applicationCode, CancellationToken ct) =>
        await EnqueueBrandedAsync(payload, await EmailBranding.ForCodeAsync(_db, applicationCode, ct), ct);

    private async Task EnqueueBrandedAsync(EmailPayload payload, EmailBrand? brand, CancellationToken ct)
    {
        payload = payload with { ApplicationDisplayName = brand?.Name, ApplicationSupportUrl = brand?.SupportUrl };
        var now = DateTime.UtcNow;
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = MessageType,
            ProtectedPayload = _protector.Protect(JsonSerializer.Serialize(payload)),
            CreatedAt = now,
            NextAttemptAt = now
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A queued email. Fields added later are optional, so that messages queued before them still
    /// deserialize: the application's display name and support link (none when it must not be named)
    /// and how long the code works, when the sender said so.
    /// </summary>
    internal sealed record EmailPayload(
        string Kind,
        string ToEmail,
        string ToName,
        string Secret,
        string? ActionUrl,
        string? ApplicationName,
        string? ActionLabel = null,
        string? ApplicationDisplayName = null,
        string? ApplicationSupportUrl = null,
        int? ValidMinutes = null);
}
