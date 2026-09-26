using AuthCenter.Application.Models;

namespace AuthCenter.Application.Interfaces;

/// <summary>
/// Preventive separation of duties: whether a change would give someone two roles an active rule
/// keeps apart. Only new combinations count; a user who already holds both is reported by the
/// violations list, not blocked from unrelated changes.
/// </summary>
public interface ISeparationOfDutiesChecker
{
    /// <summary>The conflict the user would have after gaining these roles directly (and losing those direct roles).</summary>
    Task<SeparationOfDutiesConflict?> CheckUserAsync(Guid userId, IReadOnlyCollection<Guid> addedRoleIds, IReadOnlyCollection<Guid>? removedDirectRoleIds = null, CancellationToken ct = default);

    /// <summary>The conflict a member of the group would have after the group gains these roles (and loses those).</summary>
    Task<SeparationOfDutiesConflict?> CheckGroupAsync(Guid groupId, IReadOnlyCollection<Guid> addedRoleIds, IReadOnlyCollection<Guid>? removedRoleIds = null, CancellationToken ct = default);

    /// <summary>The conflict the user would have after joining the group.</summary>
    Task<SeparationOfDutiesConflict?> CheckMembershipAsync(Guid groupId, Guid userId, CancellationToken ct = default);
}
