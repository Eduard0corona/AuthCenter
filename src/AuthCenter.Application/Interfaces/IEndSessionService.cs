using AuthCenter.Application.Common;
using AuthCenter.Application.Models;

namespace AuthCenter.Application.Interfaces;

/// <summary>OpenID Connect RP-Initiated Logout: validates the request and ends the single sign-on session.</summary>
public interface IEndSessionService
{
    Task<OperationResult<EndSessionResult>> BeginAsync(EndSessionRequest request, EndSessionCaller caller, CancellationToken ct = default);
    Task<OperationResult<EndSessionContext>> GetPendingAsync(string logoutId, string? browserBinding, CancellationToken ct = default);
    Task<OperationResult<EndSessionResult>> ConfirmAsync(string logoutId, EndSessionCaller caller, CancellationToken ct = default);
}
