using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Saml;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Saml;

namespace AuthCenter.Application.Interfaces;

/// <summary>The applications that sign in with SAML, AuthCenter being their identity provider.</summary>
public interface ISamlServiceProviderService
{
    Task<PagedResult<SamlServiceProviderDto>> GetAsync(SamlServiceProviderQuery query, CancellationToken ct = default);
    Task<SamlServiceProviderDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult<SamlServiceProviderDto>> CreateAsync(CreateSamlServiceProviderRequest request, CancellationToken ct = default);
    Task<OperationResult<SamlServiceProviderDto>> UpdateAsync(Guid id, UpdateSamlServiceProviderRequest request, CancellationToken ct = default);
    Task<OperationResult> DeleteAsync(Guid id, CancellationToken ct = default);
    OperationResult<SamlServiceProviderMetadataDto> ParseMetadata(ParseSamlMetadataRequest request);
    SamlIdentityProviderDto DescribeIdentityProvider();
}
