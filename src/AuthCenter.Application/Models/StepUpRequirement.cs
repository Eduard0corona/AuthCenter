using AuthCenter.Domain.Enums;

namespace AuthCenter.Application.Models;

/// <summary>
/// What the current single sign-on session still needs before an authorization request can be
/// completed: the assurance level the client's application requires (null when it is already met)
/// and the session's primary method, which a second factor is added to.
/// </summary>
public sealed record StepUpRequirement(string ApplicationCode, AuthenticationAssuranceLevel? RequiredAssurance, string PrimaryMethod);
