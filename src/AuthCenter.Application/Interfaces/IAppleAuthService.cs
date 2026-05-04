using AuthCenter.Application.Models;

namespace AuthCenter.Application.Interfaces;

public interface IAppleAuthService
{
    Task<ExternalTokenPayload?> ValidateIdTokenAsync(string idToken, CancellationToken ct = default);
}
