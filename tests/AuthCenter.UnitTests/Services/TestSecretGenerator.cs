using System.Security.Cryptography;

namespace AuthCenter.UnitTests.Services;

internal static class TestSecretGenerator
{
    public static string CreateKey(int byteCount = 32) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount));
}
