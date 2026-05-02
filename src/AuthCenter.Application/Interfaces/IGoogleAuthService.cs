using AuthCenter.Application.Models;

namespace AuthCenter.Application.Interfaces;

public interface IGoogleAuthService
{
    Task<GoogleTokenPayload?> ValidateIdTokenAsync(string idToken, CancellationToken ct = default);
}
