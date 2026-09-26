namespace AuthCenter.Domain.Common;

/// <summary>
/// An administrable record with an optimistic-concurrency version: every change increments it, and
/// an update that names an older version is rejected instead of overwriting newer changes.
/// </summary>
public interface IVersionedEntity
{
    long Version { get; set; }
}
