namespace AuthCenter.IntegrationTests;

/// <summary>
/// A fact that needs a real SQL Server. When no relational connection is available (Linux/macOS
/// without AUTHCENTER_RELATIONAL_TEST_CONNECTION) the test is reported as skipped instead of
/// silently passing, so a green run never hides relational coverage that did not execute.
/// </summary>
public sealed class RelationalFactAttribute : FactAttribute
{
    public RelationalFactAttribute()
    {
        if (!OperatingSystem.IsWindows() &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUTHCENTER_RELATIONAL_TEST_CONNECTION")))
        {
            Skip = "Requires SQL Server: set AUTHCENTER_RELATIONAL_TEST_CONNECTION.";
        }
    }
}
