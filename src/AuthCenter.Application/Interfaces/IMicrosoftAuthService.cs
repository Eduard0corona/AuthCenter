using AuthCenter.Application.Models;

namespace AuthCenter.Application.Interfaces;

public interface IMicrosoftAuthService
{
    Task<ExternalTokenPayload?> ValidateIdTokenAsync(string idToken, CancellationToken ct = default);
}
