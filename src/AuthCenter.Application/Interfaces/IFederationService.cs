using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Federation;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Federation;

namespace AuthCenter.Application.Interfaces;

public interface IFederationService
{
    Task<IReadOnlyList<FederationProviderDto>> GetProvidersAsync(Guid? applicationSystemId, CancellationToken ct = default);
    Task<OperationResult<FederationProviderDto>> CreateProviderAsync(UpsertFederationProviderRequest request, CancellationToken ct = default);
    Task<OperationResult<FederationProviderDto>> UpdateProviderAsync(Guid id, UpsertFederationProviderRequest request, CancellationToken ct = default);
    Task<OperationResult> DeleteProviderAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult> CreateRoutingRuleAsync(CreateFederationRoutingRuleRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<FederationRoutingRuleDto>> GetRoutingRulesAsync(Guid? applicationSystemId, CancellationToken ct = default);
    Task<OperationResult<FederationRoutingRuleDto>> UpdateRoutingRuleAsync(Guid id, UpdateFederationRoutingRuleRequest request, CancellationToken ct = default);
    Task<OperationResult> ReorderRoutingRulesAsync(ReorderFederationRoutingRulesRequest request, CancellationToken ct = default);
    Task<OperationResult> DeleteRoutingRuleAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult<FederationRouteResponse>> RouteAsync(FederationRouteRequest request, CancellationToken ct = default);
    Task<OperationResult<OidcFederationChallengeResponse>> BeginOidcAsync(BeginOidcFederationRequest request, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> CompleteOidcAsync(CompleteOidcFederationRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<SamlFederationChallengeResponse>> BeginSamlAsync(BeginSamlFederationRequest request, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> CompleteSamlAsync(CompleteSamlFederationRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<string>> GetSamlMetadataAsync(Guid providerId, CancellationToken ct = default);
}
