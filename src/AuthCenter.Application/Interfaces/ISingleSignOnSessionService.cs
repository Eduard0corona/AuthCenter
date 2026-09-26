using AuthCenter.Application.Common;

namespace AuthCenter.Application.Interfaces;

/// <summary>
/// Ends hosted-login single sign-on sessions together with everything they authorized: the
/// session record, the OAuth grants it produced and a back-channel logout notification to every
/// client that received tokens through it.
/// </summary>
public interface ISingleSignOnSessionService
{
    Task<OperationResult> EndSessionAsync(Guid userId, Guid sessionId, string reason, CancellationToken ct = default);
}
