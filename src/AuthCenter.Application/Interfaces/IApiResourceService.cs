using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.ApiResources;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.ApiResources;

namespace AuthCenter.Application.Interfaces;

/// <summary>The catalog of APIs (RFC 8707 resources) and the scopes clients can request for them.</summary>
public interface IApiResourceService
{
    Task<PagedResult<ApiResourceResponse>> GetAllAsync(ApiResourceQuery query, CancellationToken ct = default);
    Task<ApiResourceResponse?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult<ApiResourceResponse>> CreateAsync(CreateApiResourceRequest request, CancellationToken ct = default);
    Task<OperationResult<ApiResourceResponse>> UpdateAsync(Guid id, UpdateApiResourceRequest request, CancellationToken ct = default);
}
