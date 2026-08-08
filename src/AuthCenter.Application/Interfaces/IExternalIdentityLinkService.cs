using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Auth;

namespace AuthCenter.Application.Interfaces;

public interface IExternalIdentityLinkService
{
    Task<OperationResult> LinkAsync(
        Guid userId,
        LinkExternalProviderRequest request,
        CancellationToken ct = default);
}
