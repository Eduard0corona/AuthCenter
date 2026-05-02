using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Infrastructure.Settings;
using Google.Apis.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services;

public class GoogleAuthService : IGoogleAuthService
{
    private readonly GoogleAuthSettings _settings;
    private readonly ILogger<GoogleAuthService> _logger;

    public GoogleAuthService(IOptions<GoogleAuthSettings> settings, ILogger<GoogleAuthService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<GoogleTokenPayload?> ValidateIdTokenAsync(string idToken, CancellationToken ct = default)
    {
        try
        {
            var settings = new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = [_settings.ClientId]
            };

            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);

            if (!payload.EmailVerified)
            {
                _logger.LogWarning("Google token rejected: email not verified for subject {Subject}", payload.Subject);
                return null;
            }

            return new GoogleTokenPayload
            {
                Subject = payload.Subject,
                Email = payload.Email,
                EmailVerified = payload.EmailVerified,
                Name = payload.Name,
                PictureUrl = payload.Picture
            };
        }
        catch (InvalidJwtException ex)
        {
            _logger.LogWarning(ex, "Invalid Google ID token");
            return null;
        }
    }
}
