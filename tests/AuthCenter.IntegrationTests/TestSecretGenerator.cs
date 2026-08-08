using System.Security.Cryptography;

namespace AuthCenter.IntegrationTests;

internal static class TestSecretGenerator
{
    public static string CreateKey(int byteCount = 32) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount));

    public static string CreatePassword() => $"Aa1!{CreateKey(16)}";

    public static string CreateRsaPrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
}
