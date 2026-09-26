using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// SAML-01: AuthCenter as the SAML 2.0 identity provider of applications that only speak SAML —
/// metadata, SP- and IdP-initiated single sign-on through the hosted login and the same access gate
/// as OpenID Connect, signed and encrypted assertions, and single logout.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class SamlIdentityProviderTests : IClassFixture<FederationAuthCenterFactory>
{
    private const string Authority = HttpsAuthCenterFactory.Authority;
    private const string SingleSignOn = Authority + "/saml/idp/sso";
    private const string SingleLogout = Authority + "/saml/idp/slo";
    private const string Protocol = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string Assertion = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string EmailFormat = "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress";
    private const string PersistentFormat = "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent";
    private readonly FederationAuthCenterFactory _factory;

    public SamlIdentityProviderTests(FederationAuthCenterFactory factory) => _factory = factory;

    [Fact]
    public async Task Metadata_PublishesTheEndpointsAndTheSigningCertificate()
    {
        using var client = CreateBrowser();
        var response = await client.GetAsync("/saml/idp/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/samlmetadata+xml", response.Content.Headers.ContentType?.MediaType);
        var metadata = new XmlDocument();
        metadata.LoadXml(await response.Content.ReadAsStringAsync());
        Assert.Equal($"{Authority}/saml/idp/metadata", metadata.DocumentElement!.GetAttribute("entityID"));
        Assert.All(metadata.GetElementsByTagName("SingleSignOnService", "urn:oasis:names:tc:SAML:2.0:metadata").OfType<XmlElement>(),
            service => Assert.Equal(SingleSignOn, service.GetAttribute("Location")));
        var certificate = metadata.GetElementsByTagName("X509Certificate", SignedXml.XmlDsigNamespaceUrl)[0]!.InnerText;
        Assert.Equal(Convert.ToBase64String(_factory.ServiceProviderCertificate.RawData), certificate);

        using var admin = await CreateAdminClientAsync();
        var info = (await DataAsync(await admin.GetAsync("/api/saml/identity-provider")));
        Assert.True(info.GetProperty("isConfigured").GetBoolean());
        Assert.Equal(SingleLogout, info.GetProperty("singleLogoutUrl").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(_factory.ServiceProviderCertificate.RawData)), info.GetProperty("certificate").GetProperty("thumbprintSha256").GetString());
    }

    [Fact]
    public async Task ASignedInUser_GetsASignedAssertionForTheApplication_AndTheRequestIsUsedOnce()
    {
        using var admin = await CreateAdminClientAsync();
        var provider = await CreateProviderAsync(admin);
        using var browser = CreateBrowser();
        await SignInAsync(browser);

        var query = RedirectQuery(AuthnRequest(provider.EntityId, "_req1", provider.Acs), "relay-1");
        var response = await browser.GetAsync($"/saml/idp/sso?{query}");
        var (action, fields) = await FormAsync(response);
        Assert.Equal(provider.Acs, action);
        Assert.Equal("relay-1", fields["RelayState"]);
        Assert.Contains("form-action https://sp.example.test", response.Headers.GetValues("Content-Security-Policy").Single());

        var document = Decode(fields["SAMLResponse"]);
        var root = document.DocumentElement!;
        Assert.Equal(provider.Acs, root.GetAttribute("Destination"));
        Assert.Equal("_req1", root.GetAttribute("InResponseTo"));
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:status:Success", Single(root, Protocol, "StatusCode").GetAttribute("Value"));
        AssertSigned(root, _factory.ServiceProviderCertificate);
        var assertion = Single(root, Assertion, "Assertion");
        AssertSigned(assertion, _factory.ServiceProviderCertificate);
        Assert.Equal($"{Authority}/saml/idp/metadata", Single(assertion, Assertion, "Issuer").InnerText);
        Assert.Equal(provider.EntityId, Single(assertion, Assertion, "Audience").InnerText);
        var nameId = Single(assertion, Assertion, "NameID");
        Assert.Equal(EmailFormat, nameId.GetAttribute("Format"));
        Assert.Equal(AuthCenterWebApplicationFactory.AdminEmail, nameId.InnerText);
        var confirmation = Single(assertion, Assertion, "SubjectConfirmationData");
        Assert.Equal(provider.Acs, confirmation.GetAttribute("Recipient"));
        Assert.Equal("_req1", confirmation.GetAttribute("InResponseTo"));
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport", Single(assertion, Assertion, "AuthnContextClassRef").InnerText);
        var attributes = Attributes(assertion);
        Assert.Equal([AuthCenterWebApplicationFactory.AdminEmail], attributes["mail"]);
        Assert.Contains(DomainConstants.Roles.SuperAdmin, attributes["roles"]);

        await using (var scope = _factory.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().AuditLogs.AnyAsync(log => log.Action == "SAML_ASSERTION_ISSUED" && log.EntityId == provider.Id.ToString()));

        var replay = await browser.GetAsync($"/saml/idp/sso?{query}");
        await AssertErrorPageAsync(replay, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WithoutASession_TheHostedLoginContinuesTheRequest_FromTheSameBrowserOnly()
    {
        using var admin = await CreateAdminClientAsync();
        var provider = await CreateProviderAsync(admin);
        using var browser = CreateBrowser();
        var start = await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(provider.EntityId, "_req2"), "relay-2")}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var location = start.Headers.Location!.ToString();
        Assert.StartsWith("/login?saml_interaction=", location);
        var interaction = Uri.UnescapeDataString(location["/login?saml_interaction=".Length..]);

        var context = await DataAsync(await browser.GetAsync($"/saml/idp/interactions/{interaction}/context"));
        Assert.Equal(DomainConstants.SystemCodes.AuthCenter, context.GetProperty("applicationCode").GetString());
        Assert.Equal(provider.Name, context.GetProperty("clientDisplayName").GetString());
        using (var stranger = CreateBrowser())
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/saml/idp/interactions/{interaction}/context")).StatusCode);

        var csrf = await SignInAsync(browser);
        var complete = await CompleteAsync(browser, csrf, interaction);
        var redirect = (await DataAsync(complete)).GetProperty("redirectUrl").GetString()!;
        var (action, fields) = await FormAsync(await browser.GetAsync(redirect));
        Assert.Equal(provider.Acs, action);
        Assert.Equal("relay-2", fields["RelayState"]);
        Assert.Equal("_req2", Decode(fields["SAMLResponse"]).DocumentElement!.GetAttribute("InResponseTo"));
        await AssertErrorPageAsync(await browser.GetAsync(redirect), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RequestsTheIdentityProviderCannotHonor_AreAnsweredToTheProvider()
    {
        using var admin = await CreateAdminClientAsync();
        var provider = await CreateProviderAsync(admin);

        // IsPassive without a session: NoPassive, never a login page.
        using (var anonymous = CreateBrowser())
        {
            var (_, fields) = await FormAsync(await anonymous.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(provider.EntityId, "_passive", isPassive: true), null)}"));
            Assert.Equal("urn:oasis:names:tc:SAML:2.0:status:NoPassive", SubStatus(Decode(fields["SAMLResponse"])));
        }

        using var browser = CreateBrowser();
        await SignInAsync(browser);
        var (_, unsupported) = await FormAsync(await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(provider.EntityId, "_format", nameIdFormat: "urn:oasis:names:tc:SAML:2.0:nameid-format:kerberos"), null)}"));
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:status:InvalidNameIDPolicy", SubStatus(Decode(unsupported["SAMLResponse"])));

        // A user without access to the provider's application is refused, to the provider.
        var otherApplication = await CreateApplicationAsync(admin);
        var restricted = await CreateProviderAsync(admin, applicationId: otherApplication);
        var (_, denied) = await FormAsync(await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(restricted.EntityId, "_denied"), null)}"));
        var deniedResponse = Decode(denied["SAMLResponse"]);
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:status:RequestDenied", SubStatus(deniedResponse));
        Assert.Empty(deniedResponse.GetElementsByTagName("Assertion", Assertion));
    }

    [Fact]
    public async Task RequestsThatCannotBeTrusted_GetAnErrorPage_NeverAPostToTheirAddress()
    {
        using var admin = await CreateAdminClientAsync();
        var provider = await CreateProviderAsync(admin);
        var signing = TestCertificates.Create("CN=SAML test service provider");
        var signed = await CreateProviderAsync(admin, signingCertificate: signing, requireSignedRequests: true);
        using var browser = CreateBrowser();
        await SignInAsync(browser);

        await AssertErrorPageAsync(await browser.GetAsync("/saml/idp/sso?SAMLRequest=not-base64"), HttpStatusCode.BadRequest);
        await AssertErrorPageAsync(await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest("https://unknown.example.test", "_unknown"), null)}"), HttpStatusCode.BadRequest);
        await AssertErrorPageAsync(await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(provider.EntityId, "_acs", "https://attacker.example.test/acs"), null)}"), HttpStatusCode.BadRequest);
        await AssertErrorPageAsync(await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(provider.EntityId, "_old", issueInstant: DateTime.UtcNow.AddMinutes(-15)), null)}"), HttpStatusCode.BadRequest);
        await AssertErrorPageAsync(await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(provider.EntityId, "_dest", destination: "https://elsewhere.example.test/sso"), null)}"), HttpStatusCode.BadRequest);

        await AssertErrorPageAsync(await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(signed.EntityId, "_unsigned"), "r")}"), HttpStatusCode.BadRequest);
        var impostor = TestCertificates.Create("CN=Impostor");
        await AssertErrorPageAsync(await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(signed.EntityId, "_impostor"), "r", impostor)}"), HttpStatusCode.BadRequest);
        var (action, _) = await FormAsync(await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(signed.EntityId, "_signed"), "r", signing)}"));
        Assert.Equal(signed.Acs, action);
    }

    [Fact]
    public async Task ThePostBinding_ContinuesAsATopLevelGet_WithTheSignedRequest()
    {
        using var admin = await CreateAdminClientAsync();
        var signing = TestCertificates.Create("CN=SAML POST service provider");
        var provider = await CreateProviderAsync(admin, signingCertificate: signing, requireSignedRequests: true);
        using var browser = CreateBrowser();
        await SignInAsync(browser);

        var posted = await browser.PostAsync("/saml/idp/sso", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLRequest"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(SignXml(AuthnRequest(provider.EntityId, "_post"), signing))),
            ["RelayState"] = "relay-post"
        }));
        Assert.Equal(HttpStatusCode.SeeOther, posted.StatusCode);
        var (action, fields) = await FormAsync(await browser.GetAsync(posted.Headers.Location));
        Assert.Equal(provider.Acs, action);
        Assert.Equal("relay-post", fields["RelayState"]);
        Assert.Equal("_post", Decode(fields["SAMLResponse"]).DocumentElement!.GetAttribute("InResponseTo"));

        var tampered = SignXml(AuthnRequest(provider.EntityId, "_tampered"), signing).Replace("_tampered", "_changed", StringComparison.Ordinal);
        var rejected = await browser.PostAsync("/saml/idp/sso", new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLRequest"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(tampered)) }));
        await AssertErrorPageAsync(await browser.GetAsync(rejected.Headers.Location), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NameIdentifiersAndEncryption_FollowEachProvidersRegistration()
    {
        using var admin = await CreateAdminClientAsync();
        var encryption = TestCertificates.Create("CN=SAML encryption service provider");
        var first = await CreateProviderAsync(admin, nameIdFormat: PersistentFormat, encryptionCertificate: encryption);
        var second = await CreateProviderAsync(admin, nameIdFormat: PersistentFormat);
        using var browser = CreateBrowser();
        await SignInAsync(browser);

        var (_, fields) = await FormAsync(await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(first.EntityId, "_enc1"), null)}"));
        var encrypted = Decode(fields["SAMLResponse"]);
        Assert.Empty(encrypted.GetElementsByTagName("Assertion", Assertion));
        var assertion = DecryptAssertion(encrypted, encryption);
        AssertSigned(assertion, _factory.ServiceProviderCertificate);
        var firstNameId = Single(assertion, Assertion, "NameID").InnerText;
        Assert.Equal(PersistentFormat, Single(assertion, Assertion, "NameID").GetAttribute("Format"));
        Assert.DoesNotContain(AuthCenterWebApplicationFactory.AdminEmail, firstNameId);

        var (_, again) = await FormAsync(await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(first.EntityId, "_enc2"), null)}"));
        Assert.Equal(firstNameId, Single(DecryptAssertion(Decode(again["SAMLResponse"]), encryption), Assertion, "NameID").InnerText);
        var (_, other) = await FormAsync(await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(second.EntityId, "_other"), null)}"));
        Assert.NotEqual(firstNameId, Single(Decode(other["SAMLResponse"]).DocumentElement!, Assertion, "NameID").InnerText);
    }

    [Fact]
    public async Task ForcedAndStrongerAuthentication_AreMetInTheHostedLogin()
    {
        using var admin = await CreateAdminClientAsync();
        var provider = await CreateProviderAsync(admin);
        using var browser = CreateBrowser();
        var csrf = await SignInAsync(browser);

        var forced = await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(provider.EntityId, "_force", forceAuthn: true), null)}");
        Assert.Equal(HttpStatusCode.Redirect, forced.StatusCode);
        var interaction = Uri.UnescapeDataString(forced.Headers.Location!.ToString()["/login?saml_interaction=".Length..]);
        Assert.True((await DataAsync(await browser.GetAsync($"/saml/idp/interactions/{interaction}/context"))).GetProperty("requiresFreshLogin").GetBoolean());
        Assert.Equal("LOGIN_REQUIRED", await ErrorCodeAsync(await CompleteAsync(browser, csrf, interaction)));
        csrf = await SignInAsync(browser, csrf);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(browser, csrf, interaction)).StatusCode);

        // The provider asks for MFA: a password session steps up in the hosted login.
        var mfa = await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(provider.EntityId, "_mfa", context: "https://refeds.org/profile/mfa"), null)}");
        Assert.Equal(HttpStatusCode.Redirect, mfa.StatusCode);
        var stepUpInteraction = Uri.UnescapeDataString(mfa.Headers.Location!.ToString()["/login?saml_interaction=".Length..]);
        Assert.Equal("STEP_UP_REQUIRED", await ErrorCodeAsync(await CompleteAsync(browser, csrf, stepUpInteraction)));
        using var stepUp = new HttpRequestMessage(HttpMethod.Post, $"/saml/idp/interactions/{stepUpInteraction}/step-up");
        stepUp.Headers.Add("X-AuthCenter-CSRF", csrf);
        var requirement = await DataAsync(await browser.SendAsync(stepUp));
        Assert.True(requirement.GetProperty("stepUpRequired").GetBoolean());
    }

    [Fact]
    public async Task AnApplicationCanBeOpenedFromAuthCenter_WhenItAllowsIt()
    {
        using var admin = await CreateAdminClientAsync();
        var launchable = await CreateProviderAsync(admin, allowIdpInitiated: true, defaultRelayState: "/home");
        var closed = await CreateProviderAsync(admin);
        using var browser = CreateBrowser();
        await SignInAsync(browser);

        var (action, fields) = await FormAsync(await browser.GetAsync($"/saml/idp/sso/initiate/{launchable.Id}"));
        Assert.Equal(launchable.Acs, action);
        Assert.Equal("/home", fields["RelayState"]);
        Assert.False(Decode(fields["SAMLResponse"]).DocumentElement!.HasAttribute("InResponseTo"));
        await AssertErrorPageAsync(await browser.GetAsync($"/saml/idp/sso/initiate/{closed.Id}"), HttpStatusCode.NotFound);

        var applications = JsonDocument.Parse(await (await browser.GetAsync("/api/auth/applications")).Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        var authCenter = applications.EnumerateArray().Single(application => application.GetProperty("code").GetString() == DomainConstants.SystemCodes.AuthCenter);
        Assert.StartsWith("/saml/idp/sso/initiate/", authCenter.GetProperty("launchUrl").GetString());
    }

    [Fact]
    public async Task SingleLogout_EndsTheSessionTheProviderNames_AndAnswersIt()
    {
        using var admin = await CreateAdminClientAsync();
        var provider = await CreateProviderAsync(admin);
        using var browser = CreateBrowser();
        await SignInAsync(browser);
        var (_, fields) = await FormAsync(await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(provider.EntityId, "_before-logout"), null)}"));
        var issued = Decode(fields["SAMLResponse"]).DocumentElement!;
        var sessionIndex = Single(issued, Assertion, "AuthnStatement").GetAttribute("SessionIndex");

        var logout = $"""<samlp:LogoutRequest xmlns:samlp="{Protocol}" xmlns:saml="{Assertion}" ID="_logout1" Version="2.0" IssueInstant="{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}" Destination="{SingleLogout}"><saml:Issuer>{provider.EntityId}</saml:Issuer><saml:NameID Format="{EmailFormat}">{AuthCenterWebApplicationFactory.AdminEmail}</saml:NameID><samlp:SessionIndex>{sessionIndex}</samlp:SessionIndex></samlp:LogoutRequest>""";
        var response = await browser.GetAsync($"/saml/idp/slo?{RedirectQuery(logout, "after-logout")}");
        var (action, answer) = await FormAsync(response);
        Assert.Equal(provider.Slo, action);
        Assert.Equal("after-logout", answer["RelayState"]);
        var logoutResponse = Decode(answer["SAMLResponse"]).DocumentElement!;
        Assert.Equal("LogoutResponse", logoutResponse.LocalName);
        Assert.Equal("_logout1", logoutResponse.GetAttribute("InResponseTo"));
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:status:Success", Single(logoutResponse, Protocol, "StatusCode").GetAttribute("Value"));
        AssertSigned(logoutResponse, _factory.ServiceProviderCertificate);

        await using (var scope = _factory.Services.CreateAsyncScope())
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().RefreshTokens.AsNoTracking()
                .Where(token => token.Id == Guid.Parse(sessionIndex)).Select(token => token.RevokedAt).SingleAsync());
        var afterwards = await browser.GetAsync($"/saml/idp/sso?{RedirectQuery(AuthnRequest(provider.EntityId, "_after"), null)}");
        Assert.Equal(HttpStatusCode.Redirect, afterwards.StatusCode);
    }

    [Fact]
    public async Task Administration_ValidatesRegistrations_AndReadsMetadata()
    {
        using var admin = await CreateAdminClientAsync();
        var applicationId = await AuthCenterApplicationIdAsync();
        var entityId = $"https://sp.example.test/{Guid.NewGuid():N}";
        Assert.Equal("SAML_SP_INVALID", await ErrorCodeAsync(await admin.PostAsJsonAsync("/api/saml/service-providers", Registration(applicationId, entityId, acs: "http://sp.example.test/acs"))));
        Assert.Equal("SAML_SP_INVALID", await ErrorCodeAsync(await admin.PostAsJsonAsync("/api/saml/service-providers", Registration(applicationId, entityId) with { RequireSignedRequests = true })));
        Assert.Equal("SAML_SP_INVALID", await ErrorCodeAsync(await admin.PostAsJsonAsync("/api/saml/service-providers", Registration(applicationId, entityId) with { Attributes = [new AttributeSource("x", "profile:missing")] })));

        var created = await DataAsync(await admin.PostAsJsonAsync("/api/saml/service-providers", Registration(applicationId, entityId)), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetString();
        var version = created.GetProperty("version").GetInt64();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/saml/service-providers", Registration(applicationId, entityId))).StatusCode);
        var updated = await DataAsync(await admin.PutAsJsonAsync($"/api/saml/service-providers/{id}", Registration(applicationId, entityId) with { Name = "Renamed", Version = version, IsActive = true }));
        Assert.Equal("Renamed", updated.GetProperty("name").GetString());
        Assert.Equal("CONCURRENCY_CONFLICT", await ErrorCodeAsync(await admin.PutAsJsonAsync($"/api/saml/service-providers/{id}", Registration(applicationId, entityId) with { Version = version, IsActive = true })));

        var signing = TestCertificates.Create("CN=Metadata signing");
        var metadata = $"""
            <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" xmlns:ds="http://www.w3.org/2000/09/xmldsig#" entityID="https://crm.example.test/saml">
              <md:SPSSODescriptor AuthnRequestsSigned="true" protocolSupportEnumeration="{Protocol}">
                <md:KeyDescriptor use="signing"><ds:KeyInfo><ds:X509Data><ds:X509Certificate>{Convert.ToBase64String(signing.RawData)}</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>
                <md:SingleLogoutService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="https://crm.example.test/saml/slo"/>
                <md:NameIDFormat>{PersistentFormat}</md:NameIDFormat>
                <md:AssertionConsumerService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Artifact" Location="https://crm.example.test/saml/artifact" index="0"/>
                <md:AssertionConsumerService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="https://crm.example.test/saml/acs2" index="2"/>
                <md:AssertionConsumerService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="https://crm.example.test/saml/acs" index="1" isDefault="true"/>
              </md:SPSSODescriptor>
            </md:EntityDescriptor>
            """;
        var parsed = await DataAsync(await admin.PostAsJsonAsync("/api/saml/service-providers/parse-metadata", new { metadataXml = metadata }));
        Assert.Equal("https://crm.example.test/saml", parsed.GetProperty("entityId").GetString());
        Assert.Equal(["https://crm.example.test/saml/acs", "https://crm.example.test/saml/acs2"], parsed.GetProperty("assertionConsumerServiceUrls").EnumerateArray().Select(url => url.GetString()));
        Assert.Equal(PersistentFormat, parsed.GetProperty("nameIdFormat").GetString());
        Assert.True(parsed.GetProperty("requireSignedRequests").GetBoolean());
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", parsed.GetProperty("signingCertificate").GetString());
        Assert.Single(parsed.GetProperty("warnings").EnumerateArray());
        Assert.Equal("SAML_METADATA_INVALID", await ErrorCodeAsync(await admin.PostAsJsonAsync("/api/saml/service-providers/parse-metadata", new { metadataXml = "<!DOCTYPE x [<!ENTITY a 'b'>]><x>&a;</x>" })));

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/saml/service-providers/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/saml/service-providers/{id}")).StatusCode);
    }

    private sealed record Provider(Guid Id, string Name, string EntityId, string Acs, string Slo);

    private sealed record AttributeSource(string Name, string Source);

    private sealed record ProviderRegistration(
        Guid ApplicationSystemId, string Name, string EntityId, string[] AssertionConsumerServiceUrls, string? SingleLogoutServiceUrl,
        string NameIdFormat, string? SigningCertificate, bool RequireSignedRequests, string? EncryptionCertificate, bool EncryptAssertions,
        AttributeSource[] Attributes, bool AllowIdpInitiated, string? DefaultRelayState, bool IsActive = true, long? Version = null);

    private static ProviderRegistration Registration(Guid applicationId, string entityId, string acs = "https://sp.example.test/acs") => new(
        applicationId, $"SAML app {Guid.NewGuid():N}"[..20], entityId, [acs], "https://sp.example.test/slo", EmailFormat, null, false, null, false,
        [new("mail", "email"), new("displayName", "name"), new("roles", "roles"), new("groups", "groups")], false, null);

    private async Task<Provider> CreateProviderAsync(
        HttpClient admin, Guid? applicationId = null, X509Certificate2? signingCertificate = null, bool requireSignedRequests = false,
        X509Certificate2? encryptionCertificate = null, string nameIdFormat = EmailFormat, bool allowIdpInitiated = false, string? defaultRelayState = null)
    {
        var entityId = $"https://sp.example.test/{Guid.NewGuid():N}";
        var registration = Registration(applicationId ?? await AuthCenterApplicationIdAsync(), entityId) with
        {
            NameIdFormat = nameIdFormat,
            SigningCertificate = signingCertificate is null ? null : Convert.ToBase64String(signingCertificate.RawData),
            RequireSignedRequests = requireSignedRequests,
            EncryptionCertificate = encryptionCertificate?.ExportCertificatePem(),
            EncryptAssertions = encryptionCertificate is not null,
            AllowIdpInitiated = allowIdpInitiated,
            DefaultRelayState = defaultRelayState
        };
        var created = await DataAsync(await admin.PostAsJsonAsync("/api/saml/service-providers", registration), HttpStatusCode.Created);
        return new Provider(Guid.Parse(created.GetProperty("id").GetString()!), registration.Name, entityId, registration.AssertionConsumerServiceUrls[0], registration.SingleLogoutServiceUrl!);
    }

    private static string AuthnRequest(string issuer, string id, string? acs = null, bool forceAuthn = false, bool isPassive = false,
        string? nameIdFormat = null, string? context = null, DateTime? issueInstant = null, string? destination = SingleSignOn)
    {
        var instant = (issueInstant ?? DateTime.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var destinationAttribute = destination is null ? string.Empty : $" Destination=\"{destination}\"";
        var acsAttributes = acs is null ? string.Empty : $" AssertionConsumerServiceURL=\"{acs}\" ProtocolBinding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST\"";
        var policy = nameIdFormat is null ? string.Empty : $"<samlp:NameIDPolicy Format=\"{nameIdFormat}\" AllowCreate=\"true\"/>";
        var requested = context is null ? string.Empty : $"<samlp:RequestedAuthnContext Comparison=\"exact\"><saml:AuthnContextClassRef>{context}</saml:AuthnContextClassRef></samlp:RequestedAuthnContext>";
        return $"""<samlp:AuthnRequest xmlns:samlp="{Protocol}" xmlns:saml="{Assertion}" ID="{id}" Version="2.0" IssueInstant="{instant}"{destinationAttribute}{acsAttributes} ForceAuthn="{(forceAuthn ? "true" : "false")}" IsPassive="{(isPassive ? "true" : "false")}"><saml:Issuer>{issuer}</saml:Issuer>{policy}{requested}</samlp:AuthnRequest>""";
    }

    /// <summary>The HTTP-Redirect binding: DEFLATE, base64, and optionally the query signature.</summary>
    private static string RedirectQuery(string xml, string? relayState, X509Certificate2? signer = null)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(Encoding.UTF8.GetBytes(xml));
        var query = "SAMLRequest=" + Uri.EscapeDataString(Convert.ToBase64String(output.ToArray()));
        if (relayState is not null)
            query += "&RelayState=" + Uri.EscapeDataString(relayState);
        if (signer is null)
            return query;
        query += "&SigAlg=" + Uri.EscapeDataString("http://www.w3.org/2001/04/xmldsig-more#rsa-sha256");
        using var key = signer.GetRSAPrivateKey()!;
        return query + "&Signature=" + Uri.EscapeDataString(Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(query), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
    }

    /// <summary>An enveloped signature after the issuer, as service providers sign POST-binding requests.</summary>
    private static string SignXml(string xml, X509Certificate2 signer)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);
        using var key = signer.GetRSAPrivateKey()!;
        var signed = new SignedXml(document) { SigningKey = key };
        signed.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signed.SignedInfo.SignatureMethod = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
        var reference = new Reference("#" + document.DocumentElement!.GetAttribute("ID")) { DigestMethod = "http://www.w3.org/2001/04/xmlenc#sha256" };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signed.AddReference(reference);
        signed.ComputeSignature();
        var issuer = document.DocumentElement.FirstChild!;
        document.DocumentElement.InsertAfter(document.ImportNode(signed.GetXml(), true), issuer);
        return document.OuterXml;
    }

    private static XmlDocument Decode(string samlResponse)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(Encoding.UTF8.GetString(Convert.FromBase64String(samlResponse)));
        return document;
    }

    private static void AssertSigned(XmlElement element, X509Certificate2 certificate)
    {
        var signature = element.ChildNodes.OfType<XmlElement>().Single(child => child.LocalName == "Signature" && child.NamespaceURI == SignedXml.XmlDsigNamespaceUrl);
        var signed = new SignedXml(element);
        signed.LoadXml(signature);
        Assert.Equal("#" + element.GetAttribute("ID"), ((Reference)signed.SignedInfo!.References[0]!).Uri);
        Assert.True(signed.CheckSignature(certificate, verifySignatureOnly: true));
    }

    private static XmlElement DecryptAssertion(XmlDocument response, X509Certificate2 certificate)
    {
        var encryptedData = new EncryptedData();
        encryptedData.LoadXml((XmlElement)response.GetElementsByTagName("EncryptedData", EncryptedXml.XmlEncNamespaceUrl)[0]!);
        var encryptedKey = encryptedData.KeyInfo.OfType<KeyInfoEncryptedKey>().Single().EncryptedKey!;
        using var rsa = certificate.GetRSAPrivateKey()!;
        using var aes = Aes.Create();
        aes.Key = EncryptedXml.DecryptKey(encryptedKey.CipherData.CipherValue!, rsa, useOAEP: true);
        var plain = new EncryptedXml().DecryptData(encryptedData, aes);
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(Encoding.UTF8.GetString(plain));
        return document.DocumentElement!;
    }

    private static Dictionary<string, List<string>> Attributes(XmlElement assertion) =>
        assertion.GetElementsByTagName("Attribute", Assertion).OfType<XmlElement>().ToDictionary(
            attribute => attribute.GetAttribute("Name"),
            attribute => attribute.GetElementsByTagName("AttributeValue", Assertion).OfType<XmlElement>().Select(value => value.InnerText).ToList());

    private static string SubStatus(XmlDocument response) =>
        response.GetElementsByTagName("StatusCode", Protocol).OfType<XmlElement>().Last().GetAttribute("Value");

    private static XmlElement Single(XmlNode parent, string ns, string name) =>
        (XmlElement)(parent is XmlDocument document ? document.GetElementsByTagName(name, ns) : ((XmlElement)parent).GetElementsByTagName(name, ns))[0]!;

    private static async Task<(string Action, Dictionary<string, string> Fields)> FormAsync(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK && html.Contains("<form method=\"post\"", StringComparison.Ordinal), $"{(int)response.StatusCode}: {html}");
        var action = WebUtility.HtmlDecode(Regex.Match(html, "<form method=\"post\" action=\"([^\"]+)\"").Groups[1].Value);
        var fields = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")
            .ToDictionary(match => WebUtility.HtmlDecode(match.Groups[1].Value), match => WebUtility.HtmlDecode(match.Groups[2].Value));
        return (action, fields);
    }

    private static async Task AssertErrorPageAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {html}");
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<form", html, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(Authority),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<string> SignInAsync(HttpClient browser, string? csrf = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ui-api/session/login")
        {
            Content = JsonContent.Create(new LoginRequest
            {
                Email = AuthCenterWebApplicationFactory.AdminEmail,
                Password = AuthCenterWebApplicationFactory.AdminPassword,
                ApplicationCode = DomainConstants.SystemCodes.AuthCenter
            })
        };
        if (csrf is not null)
            request.Headers.Add("X-AuthCenter-CSRF", csrf);
        return (await DataAsync(await browser.SendAsync(request))).GetProperty("csrfToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> CompleteAsync(HttpClient browser, string csrf, string interaction)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/saml/idp/interactions/{interaction}/complete");
        request.Headers.Add("X-AuthCenter-CSRF", csrf);
        return await browser.SendAsync(request);
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateAuthCenterClient();
        var login = await DataAsync(await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<Guid> CreateApplicationAsync(HttpClient admin)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var created = await DataAsync(await admin.PostAsJsonAsync("/api/applications", new { code = $"SAML{suffix}", name = $"SAML {suffix}", registrationMode = "InviteOnly", allowPasswordLogin = true }), HttpStatusCode.Created);
        return Guid.Parse(created.GetProperty("id").GetString()!);
    }

    private async Task<Guid> AuthCenterApplicationIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().ApplicationSystems
            .Where(application => application.Code == DomainConstants.SystemCodes.AuthCenter).Select(application => application.Id).SingleAsync();
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.False(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }
}
