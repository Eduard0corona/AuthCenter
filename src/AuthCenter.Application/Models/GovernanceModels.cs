namespace AuthCenter.Application.Models;

/// <summary>
/// Who decides a governance item: an administrator with the governance permission, or a user
/// acting from the portal, who may only decide for the applications they own. Nobody decides
/// their own request or reviews their own access. <paramref name="CanDecide"/> is false for an
/// administrator who may only read.
/// </summary>
public sealed record GovernanceActor(Guid UserId, bool IsAdministrator, bool CanDecide = true);

/// <summary>A change that would give a user two roles a separation of duties rule keeps apart.</summary>
public sealed record SeparationOfDutiesConflict(Guid RuleId, string RuleName, Guid UserId, string UserEmail, string FirstRoleName, string SecondRoleName)
{
    public const string ErrorCode = "SOD_CONFLICT";

    public string Message =>
        $"{UserEmail} would hold both '{FirstRoleName}' and '{SecondRoleName}', which the separation of duties rule '{RuleName}' does not allow.";

    /// <summary>The user's email, the two roles and the rule, for clients that word the message themselves.</summary>
    public IReadOnlyList<string> Details => [UserEmail, FirstRoleName, SecondRoleName, RuleName];
}
