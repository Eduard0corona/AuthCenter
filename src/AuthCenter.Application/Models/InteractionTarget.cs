namespace AuthCenter.Application.Models;

/// <summary>
/// The application an authorization interaction signs the user in to. <paramref name="ForceAuthentication"/>
/// is set when the request needs a fresh sign-in (<c>prompt=login</c>, <c>select_account</c> or <c>max_age</c>),
/// which an upstream provider must not answer from its own session.
/// </summary>
public sealed record InteractionTarget(Guid ApplicationSystemId, string ApplicationCode, string? LoginHint, bool ForceAuthentication);
