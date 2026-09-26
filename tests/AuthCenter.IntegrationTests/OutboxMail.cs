using System.Text.Json;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>An email waiting in the outbox, as the dispatcher will send it.</summary>
internal sealed record OutboxMail(string Kind, string ToEmail, string ToName, string Secret, string? ActionUrl, string? ApplicationName)
{
    public static async Task<IReadOnlyList<OutboxMail>> ReadAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<AuthCenterDbContext>();
        var protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("AuthCenter.Outbox.Email.v1");
        var messages = await db.OutboxMessages.AsNoTracking().Where(message => message.Type == "email.v1").OrderBy(message => message.CreatedAt).ToListAsync();
        return messages.Select(message => JsonSerializer.Deserialize<OutboxMail>(protector.Unprotect(message.ProtectedPayload))!).ToList();
    }
}
