using System.Security.Cryptography;
using System.Text.Json;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthCenter.Infrastructure.Security;

/// <summary>
/// Holds the RSA keys this authorization server trusts. Tokens are always signed with the active
/// key (<c>Jwt:RsaPrivateKeyPem</c>), while every key in <c>Jwt:AdditionalValidationKeysPem</c> is
/// still accepted on validation and published in the JWKS. That overlap is what makes a key
/// rotation possible without invalidating the tokens already in circulation: publish the new key
/// as additional, promote it to active, then move the old one to additional until the longest
/// token lifetime has elapsed.
/// </summary>
public sealed class RsaSigningKeyRing : IDisposable
{
    public const int MinimumKeySizeBits = 2048;

    private readonly List<RSA> _ownedKeys = [];

    // Signature providers are cached per key, and CryptoProviderFactory.Default is a process-wide
    // static whose cache outlives the keys put into it: once this ring is disposed, a provider
    // left there would still point at a disposed RSA. Owning a factory keeps that cache scoped to
    // this ring's lifetime.
    private readonly CryptoProviderFactory _cryptoProviderFactory = new();

    public RsaSigningKeyRing(IOptions<JwtSettings> jwtSettings)
    {
        var settings = jwtSettings.Value;
        var validationKeys = new List<SecurityKey>();
        var jwks = new List<Dictionary<string, string>>();

        try
        {
            if (HasValue(settings.RsaPrivateKeyPem))
            {
                var signingKey = ImportKey(settings.RsaPrivateKeyPem, requirePrivateKey: true, "Jwt:RsaPrivateKeyPem");
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
                validationKeys.Add(ToPublicKey(signingKey));
                jwks.Add(ToJwk(signingKey));
            }

            foreach (var (pem, index) in settings.AdditionalValidationKeysPem.Select((pem, i) => (pem, i)))
            {
                if (!HasValue(pem))
                    continue;

                var key = ImportKey(pem, requirePrivateKey: false, $"Jwt:AdditionalValidationKeysPem:{index}");
                var publicKey = ToPublicKey(key);

                // A key repeated across the active and additional slots would be published twice.
                if (validationKeys.Any(existing => existing.KeyId == publicKey.KeyId))
                    continue;

                validationKeys.Add(publicKey);
                jwks.Add(ToJwk(key));
            }
        }
        catch
        {
            Dispose();
            throw;
        }

        ValidationKeys = validationKeys;
        Jwks = JsonSerializer.Serialize(new { keys = jwks });
    }

    /// <summary>Credentials for the active signing key, or null when no key is configured.</summary>
    public SigningCredentials? SigningCredentials { get; }

    /// <summary>Every public key a token may be signed with, active and retired alike.</summary>
    public IReadOnlyList<SecurityKey> ValidationKeys { get; }

    /// <summary>The JWKS document served at <c>/.well-known/jwks.json</c>.</summary>
    public string Jwks { get; }

    public bool IsConfigured => SigningCredentials is not null;

    public SigningCredentials RequireSigningCredentials()
    {
        return SigningCredentials ?? throw new InvalidOperationException(
            "RSA signing is unavailable. Configure Jwt:RsaPrivateKeyPem with a valid RSA private key in PEM format.");
    }

    /// <summary>
    /// Reports the first problem with the configured keys, or null when they are all usable.
    /// Used to fail startup before the first request rather than when a token is first signed.
    /// </summary>
    public static string? DescribeConfigurationError(JwtSettings settings)
    {
        if (!HasValue(settings.RsaPrivateKeyPem))
        {
            return "Jwt:RsaPrivateKeyPem must be configured with a valid RSA private key of at least " +
                   $"{MinimumKeySizeBits} bits in PEM format and must not use a placeholder value.";
        }

        try
        {
            using var probe = new RsaSigningKeyRing(Options.Create(settings));
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or CryptographicException)
        {
            return exception.Message;
        }
    }

    private RsaSecurityKey ImportKey(string pem, bool requirePrivateKey, string settingName)
    {
        var rsa = RSA.Create();
        _ownedKeys.Add(rsa);

        try
        {
            rsa.ImportFromPem(pem);

            if (rsa.KeySize < MinimumKeySizeBits)
            {
                throw new InvalidOperationException(
                    $"{settingName} must contain an RSA key of at least {MinimumKeySizeBits} bits, but it is {rsa.KeySize} bits.");
            }

            if (requirePrivateKey)
                _ = rsa.ExportParameters(true);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new InvalidOperationException(
                $"{settingName} must contain a valid RSA {(requirePrivateKey ? "private" : "public or private")} key of at least " +
                $"{MinimumKeySizeBits} bits in PEM format.",
                exception);
        }

        var key = new RsaSecurityKey(rsa) { CryptoProviderFactory = _cryptoProviderFactory };
        key.KeyId = Base64UrlEncoder.Encode(key.ComputeJwkThumbprint());
        return key;
    }

    /// <summary>
    /// Strips the private half so that a validation key or a JWKS entry can never carry it.
    /// </summary>
    private RsaSecurityKey ToPublicKey(RsaSecurityKey key)
        => new(key.Rsa!.ExportParameters(false))
        {
            KeyId = key.KeyId,
            CryptoProviderFactory = _cryptoProviderFactory
        };

    private static Dictionary<string, string> ToJwk(RsaSecurityKey key)
    {
        var parameters = key.Rsa!.ExportParameters(false);
        return new Dictionary<string, string>
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = SecurityAlgorithms.RsaSha256,
            ["kid"] = key.KeyId,
            ["n"] = Base64UrlEncoder.Encode(parameters.Modulus!),
            ["e"] = Base64UrlEncoder.Encode(parameters.Exponent!)
        };
    }

    private static bool HasValue(string? pem)
        => !string.IsNullOrWhiteSpace(pem) &&
           !pem.StartsWith("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (var key in _ownedKeys)
            key.Dispose();

        _ownedKeys.Clear();
    }
}
