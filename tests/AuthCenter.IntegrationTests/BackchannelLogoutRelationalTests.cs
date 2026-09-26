using System.IdentityModel.Tokens.Jwt;
using System.Net;
using AuthCenter.Application.Interfaces;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Security;
using AuthCenter.Infrastructure.Services;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// The outbox delivers back-channel logout notifications on SQL Server: a fresh logout token is
/// posted to the client's registered URI, and failed deliveries are retried with a new token.
/// </summary>
public sealed class BackchannelLogoutRelationalTests
{
    private const string Issuer = "https://authcenter.test";

    [RelationalFact]
    public async Task QueuedNotification_IsPostedWithAFreshLogoutToken_AndRetriedAfterAFailure()
    {
        var connectionString = SqlServerHardeningTests.BuildIsolatedConnectionString();
        var options = SqlServerHardeningTests.CreateOptions(connectionString);
        var receiver = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        var protection = new EphemeralDataProtectionProvider();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        try
        {
            await using (var setup = new AuthCenterDbContext(options))
            {
                await setup.Database.MigrateAsync();
                var application = await setup.ApplicationSystems.FirstOrDefaultAsync() ?? new ApplicationSystem
                {
                    Id = Guid.NewGuid(),
                    Code = "BCL",
                    Name = "Back-channel logout",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                if (setup.Entry(application).State == EntityState.Detached)
                    setup.ApplicationSystems.Add(application);
                var client = new OAuthClient
                {
                    Id = Guid.NewGuid(),
                    ApplicationSystemId = application.Id,
                    ClientId = "bcl-client",
                    DisplayName = "Back-channel client",
                    ClientType = OAuthClientType.Public,
                    RedirectUrisJson = "[\"https://rp.example/callback\"]",
                    AllowedScopesJson = "[\"openid\"]",
                    GrantTypesJson = "[\"authorization_code\"]",
                    LoginUrl = "https://authcenter.test/login",
                    BackchannelLogoutUri = "https://rp.example/backchannel-logout",
                    CreatedAt = DateTime.UtcNow
                };
                setup.OAuthClients.Add(client);
                setup.SingleSignOnSessionClients.Add(new SingleSignOnSessionClient
                {
                    SessionId = sessionId,
                    OAuthClientId = client.Id,
                    UserId = userId,
                    CreatedAt = DateTime.UtcNow,
                    LastIssuedAt = DateTime.UtcNow
                });
                await setup.SaveChangesAsync();

                var queue = new BackchannelLogoutQueue(setup, protection, new DateTimeProvider());
                Assert.Equal(1, await queue.EnqueueAsync(userId, [sessionId], null, CancellationToken.None));
                await setup.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddScoped(_ => new AuthCenterDbContext(options));
            services.AddSingleton<IDataProtectionProvider>(protection);
            services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
            services.AddScoped<BackchannelLogoutQueue>();
            services.AddSingleton(Options.Create(new JwtSettings { Issuer = Issuer, RsaPrivateKeyPem = TestRsaKey.PrivateKeyPem }));
            services.AddSingleton(Options.Create(new MfaSettings()));
            services.AddSingleton<RsaSigningKeyRing>();
            services.AddScoped<ITokenService, TokenService>();
            services.AddHttpClient(BackchannelLogoutQueue.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => receiver);
            await using var provider = services.BuildServiceProvider();
            var dispatcher = new OutboxDispatcherService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                protection,
                provider.GetRequiredService<IHttpClientFactory>(),
                NullLogger<OutboxDispatcherService>.Instance);

            await dispatcher.DispatchBatchAsync(CancellationToken.None);
            await using (var afterFailure = new AuthCenterDbContext(options))
            {
                var message = await afterFailure.OutboxMessages.SingleAsync(item => item.Type == BackchannelLogoutQueue.MessageType);
                Assert.Null(message.ProcessedAt);
                Assert.Equal(1, message.AttemptCount);
                message.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
                await afterFailure.SaveChangesAsync();
            }

            receiver.Status = HttpStatusCode.OK;
            await dispatcher.DispatchBatchAsync(CancellationToken.None);

            await using (var afterSuccess = new AuthCenterDbContext(options))
                Assert.NotNull((await afterSuccess.OutboxMessages.SingleAsync(item => item.Type == BackchannelLogoutQueue.MessageType)).ProcessedAt);
            Assert.Equal(2, receiver.Tokens.Count);
            Assert.NotEqual(receiver.Tokens[0], receiver.Tokens[1]);
            var token = new JwtSecurityTokenHandler().ReadJwtToken(receiver.Tokens[1]);
            Assert.Equal(DomainConstants.Claims.LogoutTokenType, token.Header.Typ);
            Assert.Equal(Issuer, token.Issuer);
            Assert.Equal("bcl-client", Assert.Single(token.Audiences));
            Assert.Equal(sessionId.ToString(), token.Claims.Single(claim => claim.Type == "sid").Value);
            Assert.Equal(userId.ToString(), token.Subject);
            Assert.Equal("https://rp.example/backchannel-logout", receiver.LastUri);
        }
        finally
        {
            await using var cleanup = new AuthCenterDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public List<string> Tokens { get; } = [];
        public string? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri!.ToString();
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            Tokens.Add(Uri.UnescapeDataString(form["logout_token=".Length..]));
            return new HttpResponseMessage(Status);
        }
    }
}
