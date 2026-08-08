namespace AuthCenter.IntegrationTests;

/// <summary>
/// Process-local signing key generated for the automated tests. No private key material is stored
/// in source control and a fresh key is created for every test process.
/// </summary>
public static class TestRsaKey
{
    public static string PrivateKeyPem { get; } = TestSecretGenerator.CreateRsaPrivateKeyPem();
}
