using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses.Lifecycle;
namespace AuthCenter.Application.Interfaces;

public interface IEventHookService
{
    Task<OperationResult<EventHookSecretResponse>> CreateAsync(CreateEventHookRequest request, CancellationToken ct = default);
    Task<OperationResult> VerifyAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult> ReplayDeadLetterAsync(Guid deliveryId, CancellationToken ct = default);
    Task<IReadOnlyList<object>> GetDeliveriesAsync(bool deadLettersOnly, CancellationToken ct = default);
}
