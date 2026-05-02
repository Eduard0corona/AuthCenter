using AuthCenter.Domain.Entities;

namespace AuthCenter.Application.Interfaces;

public interface ITokenService
{
    string GenerateAccessToken(
        ApplicationUser user,
        IList<string> roles,
        IList<string> permissions,
        IList<string> applications);

    (string token, string hash) GenerateRefreshToken();

    string HashToken(string token);

    int AccessTokenExpiryMinutes { get; }
}
