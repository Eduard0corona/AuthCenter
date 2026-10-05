using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

/// <summary>How an email names an application: <see cref="Name"/> is null when it must not name it.</summary>
internal sealed record EmailBrand(string? Name, string? SupportUrl);

/// <summary>
/// The application is the brand of the emails sent for it, and AuthCenter itself stays out of sight:
/// its own application is named only once it was given a brand, and otherwise its emails name no
/// product at all. Every other application goes by its brand's display name, or else its name.
/// </summary>
internal static class EmailBranding
{
    /// <summary>The brand of the active application with that code; null when there is none.</summary>
    public static Task<EmailBrand?> ForCodeAsync(AuthCenterDbContext db, string? applicationCode, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(applicationCode)
            ? Task.FromResult<EmailBrand?>(null)
            : FindAsync(db.ApplicationSystems.Where(application => application.Code == applicationCode && application.IsActive), ct);

    /// <summary>
    /// The name emails give that application. Governance emails are always about an application, so
    /// AuthCenter's own is described when it must not be named.
    /// </summary>
    public static async Task<string> NameAsync(AuthCenterDbContext db, Guid applicationSystemId, CancellationToken ct) =>
        (await FindAsync(db.ApplicationSystems.Where(application => application.Id == applicationSystemId), ct))?.Name
        ?? "la consola de administración";

    private static async Task<EmailBrand?> FindAsync(IQueryable<ApplicationSystem> applications, CancellationToken ct)
    {
        var application = await applications.AsNoTracking()
            .Select(item => new
            {
                item.Code,
                item.Name,
                Branded = item.BrandingSettings != null,
                DisplayName = item.BrandingSettings != null ? item.BrandingSettings.DisplayName : null,
                SupportUrl = item.BrandingSettings != null ? item.BrandingSettings.SupportUrl : null
            })
            .FirstOrDefaultAsync(ct);
        if (application is null)
            return null;

        var name = string.Equals(application.Code, DomainConstants.SystemCodes.AuthCenter, StringComparison.OrdinalIgnoreCase)
            ? application.Branded ? application.DisplayName : null
            : string.IsNullOrWhiteSpace(application.DisplayName) ? application.Name : application.DisplayName;
        // Names travel into subjects and sender names, which are a single line.
        name = string.IsNullOrWhiteSpace(name) ? null : string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var support = Uri.TryCreate(application.SupportUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? application.SupportUrl
            : null;
        return new EmailBrand(name, support);
    }
}
