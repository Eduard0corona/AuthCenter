using AuthCenter.Api.Extensions;

namespace AuthCenter.Api.Services;

public enum RateLimitDimension
{
    /// <summary>The caller's IP address.</summary>
    Ip,

    /// <summary>The account named by the email in the JSON request body.</summary>
    Account,

    /// <summary>The OAuth client authenticated with HTTP Basic or named by client_id.</summary>
    Client,

    /// <summary>The caller's IP address, only when the request names no OAuth client.</summary>
    AnonymousIp
}

public readonly record struct RateLimitRule(RateLimitDimension Dimension, int PermitLimit, TimeSpan Window);

/// <summary>
/// The effective rules per policy: the defaults, replaced per policy by
/// <c>RateLimiting:Rules:{policy}</c> entries ({ Dimension, PermitLimit, WindowSeconds }).
/// </summary>
public sealed class RateLimitRules
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<RateLimitRule>> _rules;

    public RateLimitRules(IConfiguration configuration, IWebHostEnvironment environment)
    {
        // On by default everywhere except automated tests, which opt in with RateLimiting:Enabled.
        Enabled = configuration.GetValue<bool?>("RateLimiting:Enabled") ?? !environment.IsEnvironment("Testing");

        var rules = new Dictionary<string, IReadOnlyList<RateLimitRule>>(RateLimitingExtensions.DefaultRules, StringComparer.Ordinal);
        foreach (var policy in configuration.GetSection("RateLimiting:Rules").GetChildren())
        {
            var configured = policy.GetChildren().Select(rule =>
            {
                if (!Enum.TryParse<RateLimitDimension>(rule["Dimension"], true, out var dimension) ||
                    !int.TryParse(rule["PermitLimit"], out var permits) || permits < 1 ||
                    !int.TryParse(rule["WindowSeconds"], out var seconds) || seconds < 1)
                {
                    throw new InvalidOperationException($"RateLimiting:Rules:{policy.Key} needs Dimension (Ip, Account, Client, AnonymousIp), PermitLimit and WindowSeconds.");
                }
                return new RateLimitRule(dimension, permits, TimeSpan.FromSeconds(seconds));
            }).ToList();
            if (configured.Count > 0)
                rules[policy.Key] = configured;
        }
        _rules = rules;
    }

    public bool Enabled { get; }

    public IReadOnlyList<RateLimitRule> For(string policy) =>
        _rules.TryGetValue(policy, out var rules) ? rules : [];
}
