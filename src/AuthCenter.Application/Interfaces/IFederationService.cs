using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
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

    /// <summary>
    /// Administrative route simulation: evaluates every condition (domain, group, profile) for an
    /// email. It reveals directory data, so it is never exposed anonymously.
    /// </summary>
    Task<OperationResult<FederationRouteResponse>> RouteAsync(FederationRouteRequest request, CancellationToken ct = default);

    /// <summary>
    /// Home realm discovery for the hosted login. Anonymous callers only get domain-based routes;
    /// group and profile conditions apply only to the user the browser is already signed in as.
    /// </summary>
    Task<OperationResult<FederationDiscoveryResponse>> DiscoverAsync(FederationDiscoveryRequest request, FederationCaller caller, CancellationToken ct = default);

    /// <summary>Builds the upstream redirect for the hosted login, bound to the calling browser.</summary>
    Task<OperationResult<StartFederationResponse>> StartAsync(StartFederationRequest request, FederationCaller caller, CancellationToken ct = default);

    /// <summary>Handles the upstream OIDC redirect and returns the hosted-login path to continue at.</summary>
    Task<string> CompleteOidcCallbackAsync(string? state, string? code, string? error, string? ipAddress, string? userAgent, CancellationToken ct = default);

    /// <summary>
    /// Redeems, from the browser that started it, the result an upstream callback left: the
    /// application's access policy and MFA gate run before the sign-in is issued.
    /// </summary>
    Task<OperationResult<AuthResponse>> RedeemResultAsync(string handle, FederationCaller caller, CancellationToken ct = default);

    /// <summary>
    /// Redeems the result of a link started from the account portal: the upstream identity is
    /// linked to the account that started it, from the same browser and session.
    /// </summary>
    Task<OperationResult<FederationProviderSummary>> RedeemLinkAsync(string handle, FederationCaller caller, CancellationToken ct = default);

    /// <summary>Active providers of the user's applications that the user can link from the portal.</summary>
    Task<IReadOnlyList<LinkableFederationProviderResponse>> GetLinkableProvidersAsync(Guid userId, CancellationToken ct = default);

    Task<OperationResult<OidcFederationChallengeResponse>> BeginOidcAsync(BeginOidcFederationRequest request, CancellationToken ct = default);
    Task<OperationResult<AuthResponse>> CompleteOidcAsync(CompleteOidcFederationRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<SamlFederationChallengeResponse>> BeginSamlAsync(BeginSamlFederationRequest request, CancellationToken ct = default);
    Task<FederationCompletion> CompleteSamlAsync(CompleteSamlFederationRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<OperationResult<string>> GetSamlMetadataAsync(Guid providerId, CancellationToken ct = default);

    /// <summary>Checks a provider's upstream metadata, certificates and callback configuration.</summary>
    Task<OperationResult<FederationConnectionTestResponse>> TestConnectionAsync(Guid providerId, CancellationToken ct = default);

    /// <summary>The callback, entity ID and ACS values to register at upstream providers.</summary>
    FederationServiceProviderResponse GetServiceProviderInfo();
}
