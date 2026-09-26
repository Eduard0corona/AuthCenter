using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Enums;

namespace AuthCenter.Application.Models;

/// <summary>
/// How a user authenticated: the RFC 8176 methods used and the assurance level reached. It is
/// recorded on the single sign-on session and flows into OIDC auth_time, amr and acr.
/// </summary>
public sealed record AuthenticationContext(IReadOnlyList<string> Methods, AuthenticationAssuranceLevel Assurance)
{
    public static AuthenticationContext Password { get; } =
        new([DomainConstants.AuthenticationMethods.Password], AuthenticationAssuranceLevel.Password);

    public static AuthenticationContext OneTimeLink { get; } =
        new([DomainConstants.AuthenticationMethods.OneTimePassword], AuthenticationAssuranceLevel.Password);

    public static AuthenticationContext Federated { get; } =
        new([DomainConstants.AuthenticationMethods.Federated], AuthenticationAssuranceLevel.Password);

    public static AuthenticationContext Passkey { get; } = new(
        [DomainConstants.AuthenticationMethods.ProofOfPossession, DomainConstants.AuthenticationMethods.MultiFactor],
        AuthenticationAssuranceLevel.PhishingResistant);

    /// <summary>A primary method completed with a one-time second factor.</summary>
    public static AuthenticationContext WithSecondFactor(string? primaryMethod) => new(
        new[]
        {
            primaryMethod ?? DomainConstants.AuthenticationMethods.Password,
            DomainConstants.AuthenticationMethods.OneTimePassword,
            DomainConstants.AuthenticationMethods.MultiFactor
        }.Distinct(StringComparer.Ordinal).ToArray(),
        AuthenticationAssuranceLevel.Mfa);

    public string MethodsValue => string.Join(' ', Methods);

    public static IReadOnlyList<string> ParseMethods(string? value) =>
        (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static string ContextClass(AuthenticationAssuranceLevel level) => level switch
    {
        AuthenticationAssuranceLevel.PhishingResistant => DomainConstants.AuthenticationContextClasses.PhishingResistant,
        AuthenticationAssuranceLevel.Mfa => DomainConstants.AuthenticationContextClasses.MultiFactor,
        _ => DomainConstants.AuthenticationContextClasses.SingleFactor
    };

    public static AuthenticationAssuranceLevel? AssuranceFor(string contextClass) => contextClass switch
    {
        DomainConstants.AuthenticationContextClasses.PhishingResistant => AuthenticationAssuranceLevel.PhishingResistant,
        DomainConstants.AuthenticationContextClasses.MultiFactor => AuthenticationAssuranceLevel.Mfa,
        DomainConstants.AuthenticationContextClasses.SingleFactor => AuthenticationAssuranceLevel.Password,
        _ => null
    };
}
