using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Entities;

namespace AuthCenter.Application.Interfaces;

public interface IAuthenticationSessionIssuer
{
    Task<OperationResult<AuthResponse>> IssueAsync(
        ApplicationUser user,
        Guid applicationSystemId,
        string applicationCode,
        string? ipAddress,
        string? userAgent,
        string? deviceToken = null,
        AuthenticationContext? authentication = null,
        CancellationToken ct = default);
}
