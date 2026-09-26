using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Responses.OAuth;

namespace AuthCenter.Application.Interfaces;

/// <summary>
/// AuthCenter as a SAML 2.0 identity provider: sign-in requests of registered service providers,
/// answered with signed assertions once the browser's single sign-on session passes the application's
/// access gate, and logout requests that end that session.
/// </summary>
public interface ISamlIdentityProviderService
{
    SamlIdentityProviderInfo Describe();
    string? Metadata();

    /// <summary>A POST-binding message kept for the top-level GET that follows it (a cross-site POST carries no Lax cookie).</summary>
    Task<string> KeepPostedMessageAsync(string samlMessage, string? relayState, CancellationToken ct = default);
    Task<(string SamlMessage, string? RelayState)?> TakePostedMessageAsync(string key, CancellationToken ct = default);

    Task<SamlEndpointOutcome> SignInAsync(SamlSignInStart start, AuthorizationCaller caller, CancellationToken ct = default);
    Task<OperationResult<OAuthInteractionContextResponse>> GetInteractionContextAsync(string interactionId, string? browserBinding, CancellationToken ct = default);
    Task<OperationResult<StepUpRequirement>> GetStepUpRequirementAsync(string interactionId, AuthorizationCaller caller, CancellationToken ct = default);

    /// <summary>Finishes a sign-in the hosted login continued: the id of the response to post, from the same browser.</summary>
    Task<OperationResult<string>> CompleteInteractionAsync(string interactionId, AuthorizationCaller caller, CancellationToken ct = default);
    Task<SamlPostMessage?> TakePendingResponseAsync(string responseId, string? browserBinding, CancellationToken ct = default);

    Task<SamlEndpointOutcome> LogoutAsync(SamlSignInStart request, AuthorizationCaller caller, CancellationToken ct = default);
}
