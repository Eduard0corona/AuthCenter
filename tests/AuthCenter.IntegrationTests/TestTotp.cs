using OtpNet;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// Authenticator codes for tests. AuthCenter accepts each time step once per user (replay
/// protection), so every call returns a code of a step not used before for that secret: the
/// current one, then the next and the previous, all inside the server's verification window.
/// </summary>
internal static class TestTotp
{
    private static readonly Dictionary<string, HashSet<long>> UsedSteps = new(StringComparer.Ordinal);

    public static string Code(string secretBase32)
    {
        lock (UsedSteps)
        {
            if (!UsedSteps.TryGetValue(secretBase32, out var used))
                UsedSteps[secretBase32] = used = [];
            var current = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            foreach (var step in new[] { current, current + 1, current - 1 })
            {
                if (used.Add(step))
                    return new Totp(Base32Encoding.ToBytes(secretBase32)).ComputeTotp(DateTime.UnixEpoch.AddSeconds(step * 30));
            }
            throw new InvalidOperationException("Every time step inside the verification window was already used for this secret.");
        }
    }
}
