namespace AuthCenter.Domain.Enums;

/// <summary>Where a request for access to an application came from.</summary>
public enum AccessRequestSource
{
    /// <summary>The user asked for it in the account portal, with a justification.</summary>
    Portal = 0,
    /// <summary>Self-registration in an application whose registrations need approval.</summary>
    Registration = 1,
    /// <summary>An administrator created or invited the user with access pending approval.</summary>
    Administrator = 2
}
