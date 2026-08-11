using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace AuthCenter.Client;

internal sealed class ProtectedDistributedTicketStore : ITicketStore
{
    private const string KeyPrefix = "authcenter:bff:ticket:";
    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly AuthCenterBffOptions _options;

    public ProtectedDistributedTicketStore(
        IDistributedCache cache,
        IDataProtectionProvider dataProtection,
        AuthCenterBffOptions options)
    {
        _cache = cache;
        _protector = dataProtection.CreateProtector("AuthCenter.Client.BffTicketStore.v1");
        _options = options;
    }

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = KeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        await RenewAsync(key, ticket);
        return key;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(ticket);

        var serialized = TicketSerializer.Default.Serialize(ticket);
        var protectedTicket = _protector.Protect(serialized);
        var expiration = ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.Add(_options.SessionLifetime);
        return _cache.SetAsync(key, protectedTicket, new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = expiration
        });
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var protectedTicket = await _cache.GetAsync(key);
        if (protectedTicket is null) return null;

        try
        {
            var serialized = _protector.Unprotect(protectedTicket);
            return TicketSerializer.Default.Deserialize(serialized);
        }
        catch (CryptographicException)
        {
            await _cache.RemoveAsync(key);
            return null;
        }
    }

    public Task RemoveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _cache.RemoveAsync(key);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
