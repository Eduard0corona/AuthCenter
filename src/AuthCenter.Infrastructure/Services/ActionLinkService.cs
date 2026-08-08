using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services;

public sealed class ActionLinkService : IActionLinkService
{
    private readonly ActionLinkSettings _settings;

    public ActionLinkService(IOptions<ActionLinkSettings> settings)
    {
        _settings = settings.Value;
    }

    public string GetActionUrl(ActionLinkPurpose purpose, string? applicationCode = null)
    {
        var configuredBaseUrl = applicationCode is not null &&
                                _settings.ApplicationBaseUrls.TryGetValue(applicationCode, out var applicationBaseUrl)
            ? applicationBaseUrl
            : _settings.DefaultBaseUrl;

        if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"No valid action-link base URL is configured for application '{applicationCode ?? "default"}'.");
        }

        var path = purpose switch
        {
            ActionLinkPurpose.PasswordReset => _settings.PasswordResetPath,
            ActionLinkPurpose.EmailConfirmation => _settings.EmailConfirmationPath,
            ActionLinkPurpose.Invitation => _settings.InvitationPath,
            ActionLinkPurpose.EmailChange => _settings.EmailChangePath,
            ActionLinkPurpose.MagicLink => _settings.MagicLinkPath,
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null)
        };

        return new Uri(baseUri, path.TrimStart('/')).AbsoluteUri;
    }
}
