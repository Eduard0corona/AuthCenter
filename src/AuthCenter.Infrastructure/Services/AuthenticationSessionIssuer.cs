using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Entities;

namespace AuthCenter.Infrastructure.Services;

public sealed class AuthenticationSessionIssuer : IAuthenticationSessionIssuer
{
    private readonly IRoleService _roles;
    private readonly ITokenService _tokens;
    private readonly IRefreshTokenService _refreshTokens;

    public AuthenticationSessionIssuer(
        IRoleService roles,
        ITokenService tokens,
        IRefreshTokenService refreshTokens)
    {
        _roles = roles;
        _tokens = tokens;
        _refreshTokens = refreshTokens;
    }

    public async Task<OperationResult<AuthResponse>> IssueAsync(
        ApplicationUser user,
        Guid applicationSystemId,
        string applicationCode,
        string? ipAddress,
        string? userAgent,
        string? deviceToken = null,
        CancellationToken ct = default)
    {
        var applications = new List<string> { applicationCode };
        var roles = await _roles.GetRoleNamesForUserAsync(user.Id, applicationSystemId, ct);
        var permissions = await _roles.GetPermissionCodesForUserAsync(user.Id, applicationSystemId, ct);
        var (rawRefresh, refreshHash) = _tokens.GenerateRefreshToken();
        var refreshToken = await _refreshTokens.CreateAsync(user.Id, applicationCode, refreshHash, ipAddress, userAgent, ct);
        var accessToken = _tokens.GenerateAccessToken(user, roles, permissions, applications, refreshToken.Id);

        return OperationResult<AuthResponse>.Success(new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = rawRefresh,
            ExpiresIn = _tokens.AccessTokenExpiryMinutes * 60,
            DeviceToken = deviceToken,
            User = new AuthenticatedUserDto
            {
                Id = user.Id,
                FullName = user.FullName,
                Email = user.Email!,
                PictureUrl = user.PictureUrl,
                Applications = applications,
                Roles = roles.ToList(),
                Permissions = permissions.ToList()
            }
        });
    }
}
