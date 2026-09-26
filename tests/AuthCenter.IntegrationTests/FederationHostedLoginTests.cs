using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Federation;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Federation;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// Enterprise federation through the hosted login and <c>/oauth/authorize</c>, against in-process
/// fake OIDC and SAML identity providers.
/// </summary>
public sealed class FederationHostedLoginTests : IClassFixture<FederationAuthCenterFactory>
{
    private const string Redirect = "https://federation-rp.test/callback";
    private readonly FederationAuthCenterFactory _factory;

    public FederationHostedLoginTests(FederationAuthCenterFactory factory) => _factory = factory;

    [Fact]
    public async Task OidcFederation_RequestedWithIdp_SignsInThroughTheHostedLogin()
    {
        var idp = _factory.IdentityProviders.CreateOidc();
        var provider = await CreateOidcProviderAsync(idp);
        var clientId = await RegisterClientAsync();
        using var browser = CreateBrowser();
        var (interactionId, verifier) = await BeginAuthorizationAsync(browser, clientId, new() { ["idp"] = provider.Id.ToString() });

        var context = await ReadDataAsync(await browser.GetAsync($"/oauth/interactions/{interactionId}/context"));
        Assert.True(context.GetProperty("federationAvailable").GetBoolean());
        Assert.Equal(provider.Id, context.GetProperty("identityProvider").GetProperty("id").GetGuid());
        Assert.Equal("Oidc", context.GetProperty("identityProvider").GetProperty("protocol").GetString());

        var start = await StartAsync(browser, new { providerId = provider.Id, interactionId });
        var upstream = QueryHelpers.ParseQuery(new Uri(start).Query);
        Assert.Equal(FederationAuthCenterFactory.OidcCallbackUrl, upstream["redirect_uri"].ToString());
        Assert.False(upstream.ContainsKey("prompt"));

        var email = $"fed-{Guid.NewGuid():N}@contoso.test";
        var subject = Guid.NewGuid().ToString("N");
        var callback = await browser.GetAsync(PathAndQuery(idp.SignIn(start, new FakeUpstreamUser(subject, email))));
        var handle = ResultHandle(callback, expectedInteraction: interactionId);

        var signedIn = await ReadDataAsync(await PostAsync(browser, "/ui-api/session/federation/complete", new { handle }));
        Assert.Equal(email, signedIn.GetProperty("user").GetProperty("email").GetString());
        var csrf = signedIn.GetProperty("csrfToken").GetString()!;

        var idToken = await CompleteAndExchangeAsync(browser, csrf, interactionId, clientId, verifier);
        Assert.Equal([DomainConstants.AuthenticationMethods.Federated], idToken.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value).ToArray());
        Assert.Equal(DomainConstants.AuthenticationContextClasses.SingleFactor, idToken.Claims.Single(claim => claim.Type == "acr").Value);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var user = await db.Users.SingleAsync(item => item.Email == email);
        Assert.Equal(user.Id.ToString(), idToken.Subject);
        Assert.True(user.IsExternalUser);
        Assert.True(await db.ExternalIdentityProviders.AnyAsync(item => item.UserId == user.Id && item.ProviderUserId == subject));
        Assert.True(await db.AuditLogs.AnyAsync(item => item.Action == "FEDERATION_LOGIN_SUCCESS" && item.UserId == user.Id));

        // The result is single-use.
        var replay = await PostAsync(browser, "/ui-api/session/federation/complete", new { handle }, csrf);
        Assert.Equal("FEDERATION_RESULT_INVALID", await ErrorCodeAsync(replay));
    }

    [Fact]
    public async Task FreshSignInRequest_AsksTheUpstreamToAuthenticateAgain()
    {
        var idp = _factory.IdentityProviders.CreateOidc();
        var provider = await CreateOidcProviderAsync(idp);
        var clientId = await RegisterClientAsync();
        using var browser = CreateBrowser();
        var (interactionId, _) = await BeginAuthorizationAsync(browser, clientId, new() { ["prompt"] = "login" });

        var start = await StartAsync(browser, new { providerId = provider.Id, interactionId, loginHint = "person@contoso.test" });
        var upstream = QueryHelpers.ParseQuery(new Uri(start).Query);
        Assert.Equal("login", upstream["prompt"].ToString());
        Assert.Equal("person@contoso.test", upstream["login_hint"].ToString());
    }

    [Fact]
    public async Task FederationCallback_DeliveredToAnotherBrowser_SignsNobodyIn()
    {
        var idp = _factory.IdentityProviders.CreateOidc();
        var provider = await CreateOidcProviderAsync(idp);
        var clientId = await RegisterClientAsync();
        using var attacker = CreateBrowser();
        var (interactionId, _) = await BeginAuthorizationAsync(attacker, clientId);
        var start = await StartAsync(attacker, new { providerId = provider.Id, interactionId });
        // Login CSRF: the attacker's own upstream sign-in is delivered to the victim's browser.
        var callbackUrl = idp.SignIn(start, new FakeUpstreamUser(Guid.NewGuid().ToString("N"), $"attacker-{Guid.NewGuid():N}@contoso.test"));

        using var victim = CreateBrowser();
        var handle = ResultHandle(await victim.GetAsync(PathAndQuery(callbackUrl)), expectedInteraction: interactionId);
        var redeemed = await PostAsync(victim, "/ui-api/session/federation/complete", new { handle });

        Assert.Equal(HttpStatusCode.Unauthorized, redeemed.StatusCode);
        Assert.Equal("INTERACTION_BINDING_MISMATCH", await ErrorCodeAsync(redeemed));
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/ui-api/session")).StatusCode);
    }

    [Fact]
    public async Task FederatedUserWithMfa_CompletesTheSecondFactor_UnlessTheUpstreamMfaIsTrusted()
    {
        var email = $"mfa-{Guid.NewGuid():N}@contoso.test";
        var password = TestSecretGenerator.CreatePassword();
        await CreateLocalUserAsync(email, password);
        var secret = await EnableTotpAsync(email, password);

        // Untrusted upstream MFA: the linked account still verifies its own second factor.
        var idp = _factory.IdentityProviders.CreateOidc();
        var provider = await CreateOidcProviderAsync(idp);
        using var browser = CreateBrowser();
        var handle = await FederateDirectlyAsync(browser, provider, idp, new FakeUpstreamUser(Guid.NewGuid().ToString("N"), email, Amr: ["pwd", "mfa"]));
        var pending = await ReadDataAsync(await PostAsync(browser, "/ui-api/session/federation/complete", new { handle }));
        Assert.True(pending.GetProperty("requiresMfa").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/ui-api/session")).StatusCode);
        var verified = await PostAsync(browser, "/ui-api/session/mfa", new VerifyMfaRequest
        {
            MfaPendingToken = pending.GetProperty("mfaPendingToken").GetString()!,
            TotpCode = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(DateTime.UtcNow)
        });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/ui-api/session")).StatusCode);

        // A provider trusted for MFA: the upstream MFA satisfies AuthCenter's and the ID token says so.
        var trustedIdp = _factory.IdentityProviders.CreateOidc();
        var trusted = await CreateOidcProviderAsync(trustedIdp, trustUpstreamMfa: true);
        var clientId = await RegisterClientAsync();
        using var second = CreateBrowser();
        var (interactionId, verifier) = await BeginAuthorizationAsync(second, clientId, new() { ["idp"] = trusted.Id.ToString() });
        var start = await StartAsync(second, new { providerId = trusted.Id, interactionId });
        var trustedHandle = ResultHandle(await second.GetAsync(PathAndQuery(trustedIdp.SignIn(start, new FakeUpstreamUser(Guid.NewGuid().ToString("N"), email, Amr: ["pwd", "mfa"])))), interactionId);
        var session = await ReadDataAsync(await PostAsync(second, "/ui-api/session/federation/complete", new { handle = trustedHandle }));
        Assert.False(session.TryGetProperty("requiresMfa", out _));
        var idToken = await CompleteAndExchangeAsync(second, session.GetProperty("csrfToken").GetString()!, interactionId, clientId, verifier);
        Assert.Equal(DomainConstants.AuthenticationContextClasses.MultiFactor, idToken.Claims.Single(claim => claim.Type == "acr").Value);
        Assert.Equal(
            [DomainConstants.AuthenticationMethods.Federated, DomainConstants.AuthenticationMethods.MultiFactor],
            idToken.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value).ToArray());
    }

    [Fact]
    public async Task AccessRevokedByAnAdministrator_IsNotRegrantedByFederation()
    {
        var idp = _factory.IdentityProviders.CreateOidc();
        var provider = await CreateOidcProviderAsync(idp);
        var upstreamUser = new FakeUpstreamUser(Guid.NewGuid().ToString("N"), $"revoked-{Guid.NewGuid():N}@contoso.test");
        using (var first = CreateBrowser())
        {
            var handle = await FederateDirectlyAsync(first, provider, idp, upstreamUser);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(first, "/ui-api/session/federation/complete", new { handle })).StatusCode);
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            var access = await db.UserApplicationAccesses.SingleAsync(item => item.User.Email == upstreamUser.Email && item.ApplicationSystemId == provider.ApplicationSystemId);
            access.IsActive = false;
            await db.SaveChangesAsync();
        }

        using var second = CreateBrowser();
        var denied = await PostAsync(second, "/ui-api/session/federation/complete", new { handle = await FederateDirectlyAsync(second, provider, idp, upstreamUser) });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("ACCESS_DENIED", await ErrorCodeAsync(denied));

        await using var verify = _factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        Assert.False(await verifyDb.UserApplicationAccesses.AnyAsync(item => item.User.Email == upstreamUser.Email && item.IsActive && item.ApplicationSystemId == provider.ApplicationSystemId));
        var userId = await verifyDb.Users.Where(item => item.Email == upstreamUser.Email).Select(item => item.Id).SingleAsync();
        Assert.True(await verifyDb.AuditLogs.AnyAsync(item => item.Action == "FEDERATION_ACCESS_DENIED" && item.UserId == userId));
    }

    [Fact]
    public async Task IssuerWithTrailingSlash_AndUnverifiedEmailOfARoutedDomain_AreAccepted()
    {
        var idp = _factory.IdentityProviders.CreateOidc();
        idp.Issuer = idp.BaseUrl + "/";
        var domain = $"unverified-{Guid.NewGuid():N}.test";
        var provider = await CreateOidcProviderAsync(idp, issuer: idp.BaseUrl + "/", requireVerifiedEmail: false);
        await CreateRoutingRuleAsync(provider.Id, domain);

        using var browser = CreateBrowser();
        var handle = await FederateDirectlyAsync(browser, provider, idp, new FakeUpstreamUser(Guid.NewGuid().ToString("N"), $"person@{domain}", EmailVerified: null));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(browser, "/ui-api/session/federation/complete", new { handle })).StatusCode);

        // Without email_verified, an email outside the provider's domains is not trusted to create or link accounts.
        using var other = CreateBrowser();
        var outside = await FederateDirectlyAsync(other, provider, idp, new FakeUpstreamUser(Guid.NewGuid().ToString("N"), $"person-{Guid.NewGuid():N}@elsewhere.test", EmailVerified: null));
        var rejected = await PostAsync(other, "/ui-api/session/federation/complete", new { handle = outside });
        Assert.Equal("FEDERATION_EMAIL_NOT_VERIFIED", await ErrorCodeAsync(rejected));
    }

    [Fact]
    public async Task SamlFederation_WithAnEncryptedAssertionSignedAlone_SignsInFromTheHostedLogin()
    {
        var saml = new FakeSamlProvider($"urn:test:idp:{Guid.NewGuid():N}");
        var provider = await CreateSamlProviderAsync(saml);
        var clientId = await RegisterClientAsync();
        using var browser = CreateBrowser();
        var (interactionId, verifier) = await BeginAuthorizationAsync(browser, clientId, new() { ["idp"] = provider.Id.ToString() });
        var start = await StartAsync(browser, new { providerId = provider.Id, interactionId });
        Assert.StartsWith(saml.SingleSignOnUrl, start, StringComparison.Ordinal);
        var (requestId, relayState, _) = FakeSamlProvider.ReadRequest(start);

        var email = $"saml-{Guid.NewGuid():N}@fabrikam.test";
        var response = saml.CreateResponse(requestId, $"saml-subject-{Guid.NewGuid():N}", email,
            authnContextClass: "http://schemas.microsoft.com/claims/multipleauthn", encryptFor: _factory.ServiceProviderCertificate);
        var acs = await browser.PostAsync("/api/federation/saml/acs", new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = response, ["RelayState"] = relayState }));
        Assert.True(acs.StatusCode == HttpStatusCode.SeeOther, await acs.Content.ReadAsStringAsync());
        var handle = ResultHandle(acs, interactionId);

        var session = await ReadDataAsync(await PostAsync(browser, "/ui-api/session/federation/complete", new { handle }));
        Assert.Equal(email, session.GetProperty("user").GetProperty("email").GetString());
        var idToken = await CompleteAndExchangeAsync(browser, session.GetProperty("csrfToken").GetString()!, interactionId, clientId, verifier);
        Assert.Contains(DomainConstants.AuthenticationMethods.Federated, idToken.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value));

        // The same response cannot be replayed.
        var replay = await browser.PostAsync("/api/federation/saml/acs", new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = response, ["RelayState"] = relayState }));
        Assert.Equal(HttpStatusCode.SeeOther, replay.StatusCode);
        Assert.Contains("federation_error=FEDERATION_STATE_INVALID", replay.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SamlFederation_WithATamperedAssertion_IsRejected()
    {
        var saml = new FakeSamlProvider($"http://idp-{Guid.NewGuid():N}.test/adfs/services/trust");
        var provider = await CreateSamlProviderAsync(saml);
        using var browser = CreateBrowser();
        var start = await StartAsync(browser, new { providerId = provider.Id, applicationCode = DomainConstants.SystemCodes.AuthCenter });
        var (requestId, relayState, _) = FakeSamlProvider.ReadRequest(start);
        var response = saml.CreateResponse(requestId, "subject", $"victim-{Guid.NewGuid():N}@fabrikam.test",
            tamperSignedAssertion: assertion => assertion.Replace("@fabrikam.test", "@attacker.test", StringComparison.Ordinal));

        var acs = await browser.PostAsync("/api/federation/saml/acs", new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = response, ["RelayState"] = relayState }));
        var rejected = await PostAsync(browser, "/ui-api/session/federation/complete", new { handle = ResultHandle(acs, expectedInteraction: null) });
        Assert.Equal("INVALID_SAML_SIGNATURE", await ErrorCodeAsync(rejected));
    }

    [Fact]
    public async Task DirectHostedSignIn_ReturnsToALocalPathOnly()
    {
        var idp = _factory.IdentityProviders.CreateOidc();
        var provider = await CreateOidcProviderAsync(idp);
        var domain = $"direct-{Guid.NewGuid():N}.test";
        await CreateRoutingRuleAsync(provider.Id, domain);
        using var browser = CreateBrowser();

        var route = await ReadDataAsync(await PostAsync(browser, "/ui-api/session/federation/discover", new { email = $"person@{domain}", applicationCode = DomainConstants.SystemCodes.AuthCenter }));
        Assert.True(route.GetProperty("federated").GetBoolean());
        Assert.Equal(provider.Id, route.GetProperty("provider").GetProperty("id").GetGuid());

        var start = await StartAsync(browser, new { providerId = provider.Id, applicationCode = DomainConstants.SystemCodes.AuthCenter, returnUrl = "/portal?tab=security" });
        var callback = await browser.GetAsync(PathAndQuery(idp.SignIn(start, new FakeUpstreamUser(Guid.NewGuid().ToString("N"), $"person@{domain}"))));
        var location = LocationQuery(callback);
        Assert.Equal(DomainConstants.SystemCodes.AuthCenter, location["application"].ToString());
        Assert.Equal("/portal?tab=security", location["return_url"].ToString());

        var evil = await StartAsync(browser, new { providerId = provider.Id, applicationCode = DomainConstants.SystemCodes.AuthCenter, returnUrl = "https://evil.test/steal" });
        var evilCallback = await browser.GetAsync(PathAndQuery(idp.SignIn(evil, new FakeUpstreamUser(Guid.NewGuid().ToString("N"), $"other@{domain}"))));
        Assert.False(LocationQuery(evilCallback).ContainsKey("return_url"));
    }

    [Fact]
    public async Task GroupClaims_KeepTheMappedMembershipsInSync()
    {
        var engineering = await CreateGroupAsync("Engineering");
        var sales = await CreateGroupAsync("Sales");
        var manual = await CreateGroupAsync("Manual");
        var idp = _factory.IdentityProviders.CreateOidc();
        var provider = await CreateOidcProviderAsync(idp, groupsClaim: "groups", groupMappings:
        [
            new FederationGroupMappingItem { UpstreamValue = "eng", DirectoryGroupId = engineering },
            new FederationGroupMappingItem { UpstreamValue = "sales", DirectoryGroupId = sales }
        ]);
        Assert.Equal(2, provider.GroupMappings.Count);
        var user = new FakeUpstreamUser(Guid.NewGuid().ToString("N"), $"groups-{Guid.NewGuid():N}@contoso.test", Groups: ["eng", "unmapped"]);

        using (var first = CreateBrowser())
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(first, "/ui-api/session/federation/complete", new { handle = await FederateDirectlyAsync(first, provider, idp, user) })).StatusCode);
        Guid userId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            userId = await db.Users.Where(item => item.Email == user.Email).Select(item => item.Id).SingleAsync();
            Assert.Equal([engineering], await db.UserGroupMemberships.Where(item => item.UserId == userId).Select(item => item.GroupId).ToListAsync());
            db.UserGroupMemberships.Add(new UserGroupMembership { GroupId = manual, UserId = userId, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        using (var second = CreateBrowser())
        {
            var handle = await FederateDirectlyAsync(second, provider, idp, user with { Groups = ["SALES"] });
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(second, "/ui-api/session/federation/complete", new { handle })).StatusCode);
        }
        await using var verify = _factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var memberships = await verifyDb.UserGroupMemberships.Where(item => item.UserId == userId).Select(item => item.GroupId).ToListAsync();
        Assert.Equal(new[] { sales, manual }.Order(), memberships.Order());
        Assert.True(await verifyDb.AuditLogs.AnyAsync(item => item.Action == "FEDERATION_GROUPS_SYNCED" && item.UserId == userId));
    }

    [Fact]
    public async Task HomeRealmDiscovery_UsesDomainRulesForAnonymousCallers_AndDirectoryRulesOnlyForTheSignedInUser()
    {
        var domain = $"hrd-{Guid.NewGuid():N}.test";
        var email = $"member@{domain}";
        var password = TestSecretGenerator.CreatePassword();
        await CreateLocalUserAsync(email, password);
        var group = await CreateGroupAsync("Contractors");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            db.UserGroupMemberships.Add(new UserGroupMembership { GroupId = group, UserId = await db.Users.Where(item => item.Email == email).Select(item => item.Id).SingleAsync(), CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var byDomain = await CreateOidcProviderAsync(_factory.IdentityProviders.CreateOidc());
        var byGroup = await CreateOidcProviderAsync(_factory.IdentityProviders.CreateOidc());
        await CreateRoutingRuleAsync(byDomain.Id, domain, priority: 50);
        await CreateRoutingRuleAsync(byGroup.Id, domain, priority: 5, groupId: group);

        using var anonymous = CreateBrowser();
        var probe = await ReadDataAsync(await PostAsync(anonymous, "/ui-api/session/federation/discover", new { email, applicationCode = DomainConstants.SystemCodes.AuthCenter }));
        Assert.Equal(byDomain.Id, probe.GetProperty("provider").GetProperty("id").GetGuid());

        using var signedIn = CreateBrowser();
        var login = await ReadDataAsync(await PostAsync(signedIn, "/ui-api/session/login", new LoginRequest { Email = email, Password = password, ApplicationCode = DomainConstants.SystemCodes.AuthCenter }));
        var identified = await ReadDataAsync(await PostAsync(signedIn, "/ui-api/session/federation/discover",
            new { email, applicationCode = DomainConstants.SystemCodes.AuthCenter }, login.GetProperty("csrfToken").GetString()));
        Assert.Equal(byGroup.Id, identified.GetProperty("provider").GetProperty("id").GetGuid());

        // The administrative simulation still evaluates every condition, but it is not anonymous.
        using var noToken = _factory.CreateAuthCenterClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await noToken.PostAsJsonAsync("/api/federation/route", new FederationRouteRequest { ApplicationCode = DomainConstants.SystemCodes.AuthCenter, Email = email })).StatusCode);
        using var admin = await CreateAdminClientAsync();
        var simulated = await ReadDataAsync(await admin.PostAsJsonAsync("/api/federation/route", new FederationRouteRequest { ApplicationCode = DomainConstants.SystemCodes.AuthCenter, Email = email }));
        Assert.Equal(byGroup.Id, simulated.GetProperty("providerId").GetGuid());
        Assert.Equal(5, simulated.GetProperty("matchedRulePriority").GetInt32());
    }

    [Fact]
    public async Task AuthorizeParameters_ValidateTheProviderAndExposeTheDomainHint()
    {
        var clientId = await RegisterClientAsync();
        using var browser = CreateBrowser();

        var unknown = await browser.GetAsync(AuthorizeUrl(clientId, new() { ["idp"] = Guid.NewGuid().ToString() }).Url);
        Assert.Equal("invalid_request", QueryHelpers.ParseQuery(unknown.Headers.Location!.Query)["error"].ToString());
        var malformedHint = await browser.GetAsync(AuthorizeUrl(clientId, new() { ["domain_hint"] = "not a domain" }).Url);
        Assert.Equal("invalid_request", QueryHelpers.ParseQuery(malformedHint.Headers.Location!.Query)["error"].ToString());

        var (interactionId, _) = await BeginAuthorizationAsync(browser, clientId, new() { ["domain_hint"] = "@Contoso.TEST" });
        var context = await ReadDataAsync(await browser.GetAsync($"/oauth/interactions/{interactionId}/context"));
        Assert.Equal("contoso.test", context.GetProperty("domainHint").GetString());
        Assert.False(context.TryGetProperty("identityProvider", out var provider) && provider.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task ConnectionTest_ChecksUpstreamMetadataAndCertificates()
    {
        using var admin = await CreateAdminClientAsync();
        var idp = _factory.IdentityProviders.CreateOidc();
        var oidc = await CreateOidcProviderAsync(idp);
        var oidcReport = await ReadDataAsync(await admin.PostAsync($"/api/federation/providers/{oidc.Id}/test", null));
        Assert.True(oidcReport.GetProperty("succeeded").GetBoolean(), oidcReport.ToString());
        var oidcChecks = Checks(oidcReport);
        Assert.Equal("Pass", oidcChecks["oidc.discovery"]);
        Assert.Equal("Pass", oidcChecks["oidc.issuer"]);
        Assert.Equal("Pass", oidcChecks["oidc.signing_keys"]);
        Assert.Equal("Pass", oidcChecks["oidc.callback"]);

        idp.Issuer = "https://impostor.test";
        var mismatch = await ReadDataAsync(await admin.PostAsync($"/api/federation/providers/{oidc.Id}/test", null));
        Assert.False(mismatch.GetProperty("succeeded").GetBoolean());
        Assert.Equal("Fail", Checks(mismatch)["oidc.issuer"]);

        var saml = await CreateSamlProviderAsync(new FakeSamlProvider($"urn:test:idp:{Guid.NewGuid():N}"));
        var samlChecks = Checks(await ReadDataAsync(await admin.PostAsync($"/api/federation/providers/{saml.Id}/test", null)));
        Assert.Equal("Pass", samlChecks["saml.idp_certificate"]);
        Assert.Equal("Pass", samlChecks["saml.sp_certificate"]);

        var serviceProvider = await ReadDataAsync(await admin.GetAsync("/api/federation/service-provider"));
        Assert.Equal(FederationAuthCenterFactory.OidcCallbackUrl, serviceProvider.GetProperty("oidcCallbackUrl").GetString());
        Assert.Equal(FederationAuthCenterFactory.AcsUrl, serviceProvider.GetProperty("samlAssertionConsumerServiceUrl").GetString());

        static Dictionary<string, string> Checks(JsonElement report) => report.GetProperty("checks").EnumerateArray()
            .ToDictionary(check => check.GetProperty("name").GetString()!, check => check.GetProperty("status").GetString()!);
    }

    [Fact]
    public async Task JsonApiFederation_RunsTheApplicationMfaGate()
    {
        var email = $"api-mfa-{Guid.NewGuid():N}@contoso.test";
        var password = TestSecretGenerator.CreatePassword();
        await CreateLocalUserAsync(email, password);
        await EnableTotpAsync(email, password);
        var idp = _factory.IdentityProviders.CreateOidc();
        var provider = await CreateOidcProviderAsync(idp);

        using var client = _factory.CreateAuthCenterClient();
        var begin = await ReadDataAsync(await client.PostAsJsonAsync("/api/federation/oidc/begin", new BeginOidcFederationRequest { ProviderId = provider.Id }));
        var callback = new Uri(idp.SignIn(begin.GetProperty("authorizationUrl").GetString()!, new FakeUpstreamUser(Guid.NewGuid().ToString("N"), email)));
        var query = QueryHelpers.ParseQuery(callback.Query);
        var completed = await ReadDataAsync(await client.PostAsJsonAsync("/api/federation/oidc/complete", new CompleteOidcFederationRequest
        {
            InteractionId = begin.GetProperty("interactionId").GetString()!,
            State = query["state"].ToString(),
            Code = query["code"].ToString()
        }));
        Assert.False(string.IsNullOrEmpty(completed.GetProperty("mfaPendingToken").GetString()));
        Assert.False(completed.TryGetProperty("accessToken", out _));
    }

    private async Task<FederationProviderDto> CreateOidcProviderAsync(
        FakeOidcProvider idp,
        string? issuer = null,
        bool requireVerifiedEmail = true,
        bool trustUpstreamMfa = false,
        string? groupsClaim = null,
        IReadOnlyList<FederationGroupMappingItem>? groupMappings = null)
    {
        using var admin = await CreateAdminClientAsync();
        await admin.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
        var response = await admin.PostAsJsonAsync("/api/federation/providers", new UpsertFederationProviderRequest
        {
            ApplicationSystemId = await ApplicationIdAsync(),
            Name = $"Oidc-{Guid.NewGuid():N}",
            Protocol = "Oidc",
            Issuer = issuer ?? idp.Issuer,
            ClientId = idp.ClientId,
            ClientSecret = idp.ClientSecret,
            JitProvisioningEnabled = true,
            AccountLinkingMode = "VerifiedEmail",
            RequireVerifiedEmail = requireVerifiedEmail,
            TrustUpstreamMfa = trustUpstreamMfa,
            GroupsClaim = groupsClaim,
            GroupMappings = groupMappings ?? []
        });
        var provider = await ReadAsync<FederationProviderDto>(response);
        Assert.Equal(FederationAuthCenterFactory.OidcCallbackUrl, provider.OidcCallbackUrl);
        return provider;
    }

    private async Task<FederationProviderDto> CreateSamlProviderAsync(FakeSamlProvider saml)
    {
        using var admin = await CreateAdminClientAsync();
        await admin.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
        return await ReadAsync<FederationProviderDto>(await admin.PostAsJsonAsync("/api/federation/providers", new UpsertFederationProviderRequest
        {
            ApplicationSystemId = await ApplicationIdAsync(),
            Name = $"Saml-{Guid.NewGuid():N}",
            Protocol = "Saml2",
            Issuer = saml.EntityId,
            SamlSingleSignOnUrl = saml.SingleSignOnUrl,
            SamlSigningCertificatePem = saml.CertificatePem,
            JitProvisioningEnabled = true,
            AccountLinkingMode = "VerifiedEmail"
        }));
    }

    private async Task CreateRoutingRuleAsync(Guid providerId, string domain, int priority = 10, Guid? groupId = null)
    {
        using var admin = await CreateAdminClientAsync();
        await admin.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
        var response = await admin.PostAsJsonAsync("/api/federation/routing-rules", new CreateFederationRoutingRuleRequest
        {
            FederationProviderId = providerId,
            Priority = priority,
            EmailDomain = domain,
            DirectoryGroupId = groupId
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<Guid> CreateGroupAsync(string name)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var unique = $"{name}-{Guid.NewGuid():N}";
        var group = new DirectoryGroup
        {
            Id = Guid.NewGuid(),
            Name = unique,
            NormalizedName = unique.ToUpperInvariant(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.DirectoryGroups.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    }

    /// <summary>A direct (non-OAuth) hosted sign-in at the provider; returns the result handle.</summary>
    private async Task<string> FederateDirectlyAsync(HttpClient browser, FederationProviderDto provider, FakeOidcProvider idp, FakeUpstreamUser user)
    {
        var start = await StartAsync(browser, new { providerId = provider.Id, applicationCode = DomainConstants.SystemCodes.AuthCenter });
        return ResultHandle(await browser.GetAsync(PathAndQuery(idp.SignIn(start, user))), expectedInteraction: null);
    }

    private static async Task<string> StartAsync(HttpClient browser, object request) =>
        (await ReadDataAsync(await PostAsync(browser, "/ui-api/session/federation/start", request))).GetProperty("redirectUrl").GetString()!;

    /// <summary>The callback sends the browser back to the hosted login (303) with a result handle.</summary>
    private static string ResultHandle(HttpResponseMessage callback, string? expectedInteraction)
    {
        Assert.Equal(HttpStatusCode.SeeOther, callback.StatusCode);
        var location = callback.Headers.Location!;
        Assert.StartsWith("/login?", location.OriginalString, StringComparison.Ordinal);
        var query = LocationQuery(callback);
        if (expectedInteraction is not null)
            Assert.Equal(expectedInteraction, query["interaction_id"].ToString());
        var handle = query["federation_result"].ToString();
        Assert.False(string.IsNullOrEmpty(handle), location.OriginalString);
        return handle;
    }

    private async Task<(string InteractionId, string Verifier)> BeginAuthorizationAsync(HttpClient browser, string clientId, Dictionary<string, string>? extra = null)
    {
        var (url, verifier) = AuthorizeUrl(clientId, extra);
        var authorize = await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var interactionId = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["interaction_id"].ToString();
        Assert.False(string.IsNullOrEmpty(interactionId), authorize.Headers.Location!.OriginalString);
        return (interactionId, verifier);
    }

    private async Task<JwtSecurityToken> CompleteAndExchangeAsync(HttpClient browser, string csrf, string interactionId, string clientId, string verifier)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/authorize/complete") { Content = JsonContent.Create(new { InteractionId = interactionId, Consent = true }) };
        request.Headers.Add("X-AuthCenter-CSRF", csrf);
        request.Headers.Add("X-AuthCenter-UI", "1");
        var complete = await ReadDataAsync(await browser.SendAsync(request));
        var code = QueryHelpers.ParseQuery(new Uri(complete.GetProperty("redirectUrl").GetString()!).Query)["code"].ToString();

        using var backChannel = _factory.CreateAuthCenterClient();
        var token = await backChannel.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = Redirect,
            ["code_verifier"] = verifier
        }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return new JwtSecurityTokenHandler().ReadJwtToken(json.RootElement.GetProperty("id_token").GetString());
    }

    private static (string Url, string Verifier) AuthorizeUrl(string clientId, Dictionary<string, string>? extra = null)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = Redirect,
            ["scope"] = "openid profile email",
            ["state"] = "state-value",
            ["nonce"] = "nonce-value",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256"
        };
        foreach (var (key, value) in extra ?? [])
            parameters[key] = value;
        return (QueryHelpers.AddQueryString("/oauth/authorize", parameters), verifier);
    }

    private async Task<string> RegisterClientAsync()
    {
        using var admin = await CreateAdminClientAsync();
        var clientId = $"fed-{Guid.NewGuid():N}"[..24];
        var response = await admin.PostAsJsonAsync("/api/oauth/clients", new
        {
            ApplicationSystemId = await ApplicationIdAsync(),
            ClientId = clientId,
            DisplayName = "Federation test client",
            ClientType = 1,
            RedirectUris = new[] { Redirect },
            AllowedScopes = new[] { "openid", "profile", "email" },
            GrantTypes = new[] { "authorization_code" },
            LoginUrl = $"{HttpsAuthCenterFactory.Authority}/login",
            RequirePkce = true,
            AutoConsent = true
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return clientId;
    }

    private async Task CreateLocalUserAsync(string email, string password)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = "Local User",
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            HasLocalPassword = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
        db.UserApplicationAccesses.Add(new UserApplicationAccess { Id = Guid.NewGuid(), UserId = user.Id, ApplicationSystemId = await ApplicationIdAsync(), IsActive = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<string> EnableTotpAsync(string email, string password)
    {
        using var client = _factory.CreateAuthCenterClient();
        var auth = await ReadAsync<AuthResponse>(await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password, ApplicationCode = DomainConstants.SystemCodes.AuthCenter }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        await client.AddReauthenticationProofAsync(password, "factor.enroll");
        var setup = await ReadAsync<MfaSetupResponse>(await client.PostAsync("/api/auth/mfa/setup", null));
        var enable = await client.PostAsJsonAsync("/api/auth/mfa/enable", new EnableMfaRequest { TotpCode = new Totp(Base32Encoding.ToBytes(setup.SecretBase32)).ComputeTotp(DateTime.UtcNow) });
        enable.EnsureSuccessStatusCode();
        return setup.SecretBase32;
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateAuthCenterClient();
        var auth = await ReadAsync<AuthResponse>(await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private async Task<Guid> ApplicationIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().ApplicationSystems
            .Where(application => application.Code == DomainConstants.SystemCodes.AuthCenter).Select(application => application.Id).SingleAsync();
    }

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(HttpsAuthCenterFactory.Authority),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string path, object body, string? csrf = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (csrf is not null)
            request.Headers.Add("X-AuthCenter-CSRF", csrf);
        return await browser.SendAsync(request);
    }

    private static string PathAndQuery(string url) => new Uri(url).PathAndQuery;

    // The hosted-login redirects are relative ("/login?..."), which Uri.Query does not accept.
    private static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> LocationQuery(HttpResponseMessage response)
    {
        var location = response.Headers.Location!.OriginalString;
        return QueryHelpers.ParseQuery(location.Contains('?') ? location[location.IndexOf('?')..] : string.Empty);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode;

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) where T : class
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var data = JsonSerializer.Deserialize<ApiResponse<T>>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Data;
        Assert.NotNull(data);
        return data;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("data").Clone();
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
