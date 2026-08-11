using AuthCenter.Application.Models;

namespace AuthCenter.Application.Interfaces;

public interface IAuthenticationRiskService
{
    Task<AuthenticationSignalAssessment> AssessAndRecordAsync(
        Guid userId,
        string? ipAddress,
        string? userAgent,
        decimal? latitude = null,
        decimal? longitude = null,
        CancellationToken ct = default);
}
