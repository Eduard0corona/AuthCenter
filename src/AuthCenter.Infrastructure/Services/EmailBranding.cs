using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

/// <summary>How an email names an application: <see cref="Name"/> is null when it names none.</summary>
internal sealed record EmailBrand(string? Name, string? SupportUrl);

/// <summary>
/// The application is the brand of the emails sent for it, and AuthCenter itself stays out of sight:
/// its own application under its own name is, to the people who receive them, no application at all.
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
    /// AuthCenter's own is described when it has no name of its own.
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
                DisplayName = item.BrandingSettings != null ? item.BrandingSettings.DisplayName : null,
                SupportUrl = item.BrandingSettings != null ? item.BrandingSettings.SupportUrl : null
            })
            .FirstOrDefaultAsync(ct);
        if (application is null)
            return null;

        var name = EndUserName(application.Code, application.Name, application.DisplayName);
        // Treated exactly as no application: no name and no help link either.
        if (string.IsNullOrWhiteSpace(name))
            return new EmailBrand(null, null);
        var support = Uri.TryCreate(application.SupportUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? application.SupportUrl
            : null;
        // Names travel into subjects and sender names, which are a single line.
        return new EmailBrand(string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), support);
    }

    /// <summary>
    /// The name people see for an application: its brand's display name, else its name. AuthCenter's
    /// own application still called AuthCenter (as the migrations leave it) gets none (empty).
    /// </summary>
    private static string EndUserName(string code, string name, string? brandedName)
    {
        var shown = string.IsNullOrWhiteSpace(brandedName) ? name : brandedName;
        return string.Equals(code, DomainConstants.SystemCodes.AuthCenter, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(shown.Trim(), "AuthCenter", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : shown;
    }
}
