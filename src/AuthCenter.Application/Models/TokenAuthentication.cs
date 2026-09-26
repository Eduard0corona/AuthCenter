using AuthCenter.Domain.Enums;

namespace AuthCenter.Application.Models;

/// <summary>
/// The authentication behind an OAuth grant, reported in OIDC auth_time, amr, acr and sid.
/// </summary>
public sealed record TokenAuthentication(
    DateTime AuthenticatedAt,
    IReadOnlyList<string> Methods,
    AuthenticationAssuranceLevel Assurance,
    Guid? SessionId);
