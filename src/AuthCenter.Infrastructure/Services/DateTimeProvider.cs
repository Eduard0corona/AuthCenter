using AuthCenter.Application.Interfaces;

namespace AuthCenter.Infrastructure.Services;

public class DateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}
