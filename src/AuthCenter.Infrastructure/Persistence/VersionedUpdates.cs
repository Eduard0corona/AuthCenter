using AuthCenter.Domain.Common;

namespace AuthCenter.Infrastructure.Persistence;

internal static class VersionedUpdates
{
    public const string ConflictCode = "CONCURRENCY_CONFLICT";

    /// <summary>
    /// Starts an administrative update: when the caller says which version it loaded, a record that
    /// changed since then is not overwritten. The record moves to the next version either way; a
    /// concurrent save between this check and the caller's is still refused by the database
    /// (<see cref="IVersionedEntity.Version"/> is a concurrency token).
    /// </summary>
    public static bool TryAdvance(this IVersionedEntity entity, long? loadedVersion)
    {
        if (loadedVersion.HasValue && loadedVersion.Value != entity.Version)
            return false;
        entity.Version++;
        return true;
    }
}
