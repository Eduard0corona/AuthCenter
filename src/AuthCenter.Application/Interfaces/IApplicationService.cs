using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Applications;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Applications;
using AuthCenter.Domain.Entities;

namespace AuthCenter.Application.Interfaces;

public interface IApplicationService
{
    Task<PagedResult<ApplicationDto>> GetAllAsync(PaginationQuery pagination, CancellationToken ct = default);
    Task<ApplicationDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ApplicationSystem?> GetByCodeAsync(string code, CancellationToken ct = default);
    Task<ApplicationSystem?> GetByCodeWithSettingsAsync(string code, CancellationToken ct = default);
    Task<OperationResult<ApplicationDto>> CreateAsync(CreateApplicationRequest request, CancellationToken ct = default);
    Task<OperationResult<ApplicationDto>> UpdateAsync(Guid id, UpdateApplicationRequest request, CancellationToken ct = default);
    Task<OperationResult> ActivateAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult> DeactivateAsync(Guid id, CancellationToken ct = default);
}
