namespace AuthCenter.Application.Interfaces;

public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}
