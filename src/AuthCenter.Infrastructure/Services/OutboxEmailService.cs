using System.Text.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;

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
        EnqueueAsync(new EmailPayload("password-reset", toEmail, toName, resetToken, callbackBaseUrl, null), ct);

    public Task SendEmailConfirmationAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default) =>
        EnqueueAsync(new EmailPayload("email-confirmation", toEmail, toName, token, callbackBaseUrl, null), ct);

    public Task SendInvitationAsync(string toEmail, string toName, string applicationName, string token, string? callbackBaseUrl, CancellationToken ct = default) =>
        EnqueueAsync(new EmailPayload("invitation", toEmail, toName, token, callbackBaseUrl, applicationName), ct);

    public Task SendEmailChangeConfirmationAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default) =>
        EnqueueAsync(new EmailPayload("email-change", toEmail, toName, token, callbackBaseUrl, null), ct);

    public Task SendMagicLinkAsync(string toEmail, string toName, string token, string? callbackBaseUrl, CancellationToken ct = default) =>
        EnqueueAsync(new EmailPayload("magic-link", toEmail, toName, token, callbackBaseUrl, null), ct);

    public Task SendMfaEmailOtpAsync(string toEmail, string toName, string code, CancellationToken ct = default) =>
        EnqueueAsync(new EmailPayload("mfa-otp", toEmail, toName, code, null, null), ct);

    // The subject travels in ApplicationName and the detail in Secret, keeping the payload shape stable.
    public Task SendSecurityNoticeAsync(string toEmail, string toName, string subject, string detail, CancellationToken ct = default) =>
        EnqueueAsync(new EmailPayload("security-notice", toEmail, toName, detail, null, subject), ct);

    private async Task EnqueueAsync(EmailPayload payload, CancellationToken ct)
    {
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

    internal sealed record EmailPayload(
        string Kind,
        string ToEmail,
        string ToName,
        string Secret,
        string? ActionUrl,
        string? ApplicationName);
}
