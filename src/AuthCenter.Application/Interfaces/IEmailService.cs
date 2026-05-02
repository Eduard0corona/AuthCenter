namespace AuthCenter.Application.Interfaces;

public interface IEmailService
{
    Task SendPasswordResetAsync(string toEmail, string toName, string resetToken, string? callbackBaseUrl, CancellationToken ct = default);
}
