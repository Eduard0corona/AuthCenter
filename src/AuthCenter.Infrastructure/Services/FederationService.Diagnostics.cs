using System.Security.Cryptography.X509Certificates;
using AuthCenter.Application.Common;
using AuthCenter.Contracts.Responses.Federation;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

/// <summary>Connection test of a configured provider, run by an administrator before users depend on it.</summary>
public sealed partial class FederationService
{
    private const string CheckPass = "Pass";
    private const string CheckWarning = "Warning";
    private const string CheckFail = "Fail";

    public async Task<OperationResult<FederationConnectionTestResponse>> TestConnectionAsync(Guid providerId, CancellationToken ct = default)
    {
        var provider = await _db.FederationProviders.AsNoTracking().Include(item => item.ApplicationSystem).FirstOrDefaultAsync(item => item.Id == providerId, ct);
        if (provider is null)
            return OperationResult<FederationConnectionTestResponse>.Failure("FEDERATION_PROVIDER_NOT_FOUND", "Federation provider not found.");

        var checks = new List<FederationConnectionCheck>();
        void Add(string name, string status, string detail) => checks.Add(new FederationConnectionCheck { Name = name, Status = status, Detail = detail });

        if (!provider.IsActive)
            Add("provider.active", CheckWarning, "The provider is inactive, so the hosted login does not offer it.");
        if (provider.Protocol == FederationProtocol.Oidc)
            await TestOidcAsync(provider, Add, ct);
        else
            TestSaml(provider, Add);

        var routed = await _db.FederationRoutingRules.AnyAsync(rule => rule.FederationProviderId == provider.Id && rule.IsActive && rule.EmailDomain != null, ct);
        Add("routing.domain", routed ? CheckPass : CheckWarning, routed
            ? "An active email-domain rule sends users to this provider."
            : "No active email-domain rule: anonymous users only reach this provider through the idp parameter.");

        var succeeded = checks.All(check => check.Status != CheckFail);
        await _audit.LogAsync("FEDERATION_CONNECTION_TESTED", applicationCode: provider.ApplicationSystem.Code, entityName: nameof(FederationProvider), entityId: provider.Id.ToString(),
            metadata: new { succeeded, failed = checks.Where(check => check.Status == CheckFail).Select(check => check.Name).ToList() }, ct: ct);
        return OperationResult<FederationConnectionTestResponse>.Success(new FederationConnectionTestResponse
        {
            ProviderId = provider.Id,
            Protocol = provider.Protocol.ToString(),
            Succeeded = succeeded,
            Checks = checks
        });
    }

    private async Task TestOidcAsync(FederationProvider provider, Action<string, string, string> add, CancellationToken ct)
    {
        var address = provider.DiscoveryEndpoint ?? $"{provider.Issuer.TrimEnd('/')}/.well-known/openid-configuration";
        var configuration = await _metadata.FetchAsync(address, ct);
        if (configuration is null)
        {
            add("oidc.discovery", CheckFail, $"The discovery document at {address} could not be read.");
            return;
        }
        add("oidc.discovery", CheckPass, $"Discovery document read from {address}.");

        var issuerMatches = string.Equals(configuration.Issuer?.TrimEnd('/'), provider.Issuer.TrimEnd('/'), StringComparison.Ordinal);
        add("oidc.issuer", issuerMatches ? CheckPass : CheckFail, issuerMatches
            ? "The discovery issuer matches the configured issuer."
            : $"The discovery document declares the issuer '{configuration.Issuer}', not '{provider.Issuer}'.");

        var endpoints = IsHttps(configuration.AuthorizationEndpoint) && IsHttps(configuration.TokenEndpoint);
        add("oidc.endpoints", endpoints ? CheckPass : CheckFail, endpoints
            ? "Authorization and token endpoints use HTTPS."
            : "The authorization and token endpoints must be HTTPS URLs.");

        add("oidc.signing_keys", configuration.SigningKeys.Count > 0 ? CheckPass : CheckFail, configuration.SigningKeys.Count > 0
            ? $"{configuration.SigningKeys.Count} signing key(s) published."
            : "The provider publishes no signing keys, so its ID tokens cannot be validated.");

        var supportsCode = configuration.ResponseTypesSupported.Count == 0 || configuration.ResponseTypesSupported.Contains("code");
        add("oidc.response_type", supportsCode ? CheckPass : CheckFail, supportsCode
            ? "The authorization code flow is supported."
            : "The provider does not advertise response_type=code.");

        var pkce = configuration.CodeChallengeMethodsSupported.Contains("S256");
        add("oidc.pkce", pkce ? CheckPass : CheckWarning, pkce
            ? "PKCE with S256 is supported."
            : "PKCE S256 is not advertised; AuthCenter always sends it and the provider may ignore it.");

        var hosted = IsHostedCallback(provider.OidcCallbackUrl);
        add("oidc.callback", hosted ? CheckPass : CheckWarning, hosted
            ? "The callback is AuthCenter's hosted callback."
            : $"The callback is not {HostedOidcCallbackUrl ?? HostedOidcCallbackPath}: the provider is only usable through the JSON API, not from the hosted login.");

        add("oidc.client_secret", provider.ProtectedClientSecret is not null ? CheckPass : CheckWarning, provider.ProtectedClientSecret is not null
            ? "A client secret is stored."
            : "No client secret is stored: the provider must accept a public client with PKCE.");

        var email = configuration.ScopesSupported.Count == 0 || configuration.ScopesSupported.Contains("email");
        add("oidc.email_scope", email ? CheckPass : CheckWarning, email
            ? "The email scope is available."
            : "The provider does not advertise the email scope; sign-ins need an email claim.");
    }

    private void TestSaml(FederationProvider provider, Action<string, string, string> add)
    {
        var now = _clock.UtcNow;
        if (!TryCertificate(provider.SamlSigningCertificatePem, out var certificate))
        {
            add("saml.idp_certificate", CheckFail, "The provider's signing certificate is missing or invalid.");
        }
        else
        {
            using (certificate)
            {
                var notBefore = certificate!.NotBefore.ToUniversalTime();
                var notAfter = certificate.NotAfter.ToUniversalTime();
                if (notBefore > now || notAfter <= now)
                    add("saml.idp_certificate", CheckFail, $"The provider's signing certificate is not valid now (valid {notBefore:u} – {notAfter:u}).");
                else if (notAfter <= now.AddDays(30))
                    add("saml.idp_certificate", CheckWarning, $"The provider's signing certificate expires on {notAfter:u}.");
                else
                    add("saml.idp_certificate", CheckPass, $"The provider's signing certificate is valid until {notAfter:u}.");

                using var rsa = certificate.GetRSAPublicKey();
                add("saml.idp_key", rsa is { KeySize: >= 2048 } ? CheckPass : CheckFail, rsa is { KeySize: >= 2048 }
                    ? $"RSA {rsa.KeySize}-bit signing key."
                    : "The signing certificate must carry an RSA key of at least 2048 bits.");
            }
        }

        add("saml.sso_url", IsHttps(provider.SamlSingleSignOnUrl) ? CheckPass : CheckFail, IsHttps(provider.SamlSingleSignOnUrl)
            ? "The single sign-on URL uses HTTPS."
            : "The single sign-on URL must be HTTPS.");

        var local = TryLocalSigningCertificate(out var signing);
        signing?.Dispose();
        add("saml.sp_certificate", local ? CheckPass : CheckFail, local
            ? "AuthCenter's SAML certificate is configured, valid and has its private key (signing and decryption)."
            : "Saml:SigningCertificateBase64 is missing, expired or has no private key, so AuthnRequests cannot be signed.");

        var endpoints = Uri.TryCreate(_samlSettings.EntityId, UriKind.Absolute, out _) && IsHttps(_samlSettings.AssertionConsumerServiceUrl);
        add("saml.sp_endpoints", endpoints ? CheckPass : CheckFail, endpoints
            ? $"Entity ID {_samlSettings.EntityId}, ACS {_samlSettings.AssertionConsumerServiceUrl}."
            : "Saml:EntityId and an HTTPS Saml:AssertionConsumerServiceUrl are required.");
    }
}
