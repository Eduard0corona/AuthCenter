using AuthCenter.Application.Interfaces;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AuthCenter.UnitTests.Services;

public class TransientStateStoreTests
{
    private const string Purpose = "magic_link";

    [Fact]
    public async Task TryConsume_SucceedsOnceAndRefusesEveryTimeAfter()
    {
        var (store, clock) = CreateStore();
        var expiry = clock.UtcNow.AddMinutes(15);

        Assert.True(await store.TryConsumeAsync(Purpose, "token-1", expiry));
        Assert.False(await store.TryConsumeAsync(Purpose, "token-1", expiry));
        Assert.False(await store.TryConsumeAsync(Purpose, "token-1", expiry));
    }

    [Fact]
    public async Task TryConsume_TreatsDifferentPurposesAsSeparateKeyspaces()
    {
        var (store, clock) = CreateStore();
        var expiry = clock.UtcNow.AddMinutes(15);

        Assert.True(await store.TryConsumeAsync("magic_link", "shared-id", expiry));
        Assert.True(await store.TryConsumeAsync("mfa", "shared-id", expiry));
    }

    [Fact]
    public async Task ConsumedMarker_StopsBlockingOnceItsTokenCouldNoLongerBeRedeemed()
    {
        var (store, clock) = CreateStore();

        Assert.True(await store.TryConsumeAsync(Purpose, "token-1", clock.UtcNow.AddMinutes(15)));

        clock.Advance(TimeSpan.FromMinutes(16));

        // The marker outlived the token it guarded, so keeping it would only grow the table.
        Assert.False(await store.IsConsumedAsync(Purpose, "token-1"));
        Assert.True(await store.TryConsumeAsync(Purpose, "token-1", clock.UtcNow.AddMinutes(15)));
    }

    [Fact]
    public async Task IsConsumed_DoesNotConsume()
    {
        var (store, clock) = CreateStore();
        var expiry = clock.UtcNow.AddMinutes(15);

        Assert.False(await store.IsConsumedAsync(Purpose, "token-1"));
        Assert.True(await store.TryConsumeAsync(Purpose, "token-1", expiry));
        Assert.True(await store.IsConsumedAsync(Purpose, "token-1"));
        Assert.True(await store.IsConsumedAsync(Purpose, "token-1"));
    }

    [Fact]
    public async Task SetAndGet_RoundTripsAValue()
    {
        var (store, clock) = CreateStore();

        await store.SetAsync("emailotp_setup", "user-1", "123456", clock.UtcNow.AddMinutes(10));

        Assert.Equal("123456", await store.GetAsync("emailotp_setup", "user-1"));
    }

    [Fact]
    public async Task Set_ReplacesAValueAlreadyUnderTheKey()
    {
        var (store, clock) = CreateStore();

        await store.SetAsync("emailotp_setup", "user-1", "111111", clock.UtcNow.AddMinutes(10));
        await store.SetAsync("emailotp_setup", "user-1", "222222", clock.UtcNow.AddMinutes(10));

        // Requesting a new code must invalidate the previous one rather than leave both valid.
        Assert.Equal("222222", await store.GetAsync("emailotp_setup", "user-1"));
    }

    [Fact]
    public async Task Get_ReturnsNothingOnceTheValueHasExpired()
    {
        var (store, clock) = CreateStore();
        await store.SetAsync("emailotp_setup", "user-1", "123456", clock.UtcNow.AddMinutes(10));

        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Null(await store.GetAsync("emailotp_setup", "user-1"));
    }

    [Fact]
    public async Task Take_ReturnsTheValueAndLeavesNothingBehind()
    {
        var (store, clock) = CreateStore();
        await store.SetAsync("oauth_session", "interaction-1", "{\"ClientId\":\"app\"}", clock.UtcNow.AddMinutes(10));

        Assert.Equal("{\"ClientId\":\"app\"}", await store.TakeAsync("oauth_session", "interaction-1"));
        Assert.Null(await store.TakeAsync("oauth_session", "interaction-1"));
    }

    [Fact]
    public async Task Take_OnAnExpiredEntry_ReturnsNothing()
    {
        var (store, clock) = CreateStore();
        await store.SetAsync("oauth_session", "interaction-1", "{}", clock.UtcNow.AddMinutes(10));

        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Null(await store.TakeAsync("oauth_session", "interaction-1"));
    }

    [Fact]
    public async Task Remove_ClearsTheEntry()
    {
        var (store, clock) = CreateStore();
        await store.SetAsync("emailotp_setup", "user-1", "123456", clock.UtcNow.AddMinutes(10));

        await store.RemoveAsync("emailotp_setup", "user-1");

        Assert.Null(await store.GetAsync("emailotp_setup", "user-1"));
    }

    [Fact]
    public async Task Remove_OnAMissingEntry_IsNotAnError()
    {
        var (store, _) = CreateStore();

        await store.RemoveAsync("emailotp_setup", "never-existed");
    }

    [Fact]
    public async Task ConsumedState_IsVisibleThroughAnIndependentConnection()
    {
        // The point of the whole store: a second instance of the service, which shares nothing but
        // the database, must see that the token was already redeemed.
        var dbFactory = new TestDbContextFactory();
        var clock = new MutableClock(new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc));

        var firstInstance = new TransientStateStore(dbFactory, clock);
        var secondInstance = new TransientStateStore(dbFactory, clock);

        Assert.True(await firstInstance.TryConsumeAsync(Purpose, "token-1", clock.UtcNow.AddMinutes(15)));
        Assert.False(await secondInstance.TryConsumeAsync(Purpose, "token-1", clock.UtcNow.AddMinutes(15)));
        Assert.True(await secondInstance.IsConsumedAsync(Purpose, "token-1"));
    }

    private static (ITransientStateStore Store, MutableClock Clock) CreateStore()
    {
        var clock = new MutableClock(new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc));
        return (new TransientStateStore(new TestDbContextFactory(), clock), clock);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AuthCenterDbContext>
    {
        private readonly string _databaseName = "TransientState_" + Guid.NewGuid();

        public AuthCenterDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<AuthCenterDbContext>()
                .UseInMemoryDatabase(_databaseName)
                .Options;
            return new AuthCenterDbContext(options);
        }
    }

    private sealed class MutableClock : IDateTimeProvider
    {
        public MutableClock(DateTime start) => UtcNow = start;

        public DateTime UtcNow { get; private set; }

        public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
    }
}
