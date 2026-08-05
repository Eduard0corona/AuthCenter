namespace AuthCenter.Application.Interfaces;

/// <summary>
/// Short-lived state shared by every instance of the service. Backs single-use enforcement for
/// redeemable tokens and the codes and sessions that span more than one request.
/// </summary>
public interface ITransientStateStore
{
    /// <summary>
    /// Records the key as consumed, returning false if it already was. This is the single-use
    /// gate: the first caller gets true, everyone after gets false.
    /// </summary>
    Task<bool> TryConsumeAsync(string purpose, string key, DateTime expiresAt, CancellationToken ct = default);

    /// <summary>Whether the key has been consumed, without consuming it.</summary>
    Task<bool> IsConsumedAsync(string purpose, string key, CancellationToken ct = default);

    /// <summary>Stores a value, replacing any unexpired entry already under the key.</summary>
    Task SetAsync(string purpose, string key, string value, DateTime expiresAt, CancellationToken ct = default);

    /// <summary>Reads a value, or null when it is missing or expired.</summary>
    Task<string?> GetAsync(string purpose, string key, CancellationToken ct = default);

    /// <summary>Reads a value and removes it in one step.</summary>
    Task<string?> TakeAsync(string purpose, string key, CancellationToken ct = default);

    Task RemoveAsync(string purpose, string key, CancellationToken ct = default);
}
