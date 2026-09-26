using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Lifecycle;
namespace AuthCenter.Application.Interfaces;

public interface IEventHookService
{
    Task<PagedResult<EventHookDto>> GetAsync(EventHookQuery query, CancellationToken ct = default);
    Task<EventHookDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult<EventHookSecretResponse>> CreateAsync(CreateEventHookRequest request, CancellationToken ct = default);
    Task<OperationResult<EventHookDto>> UpdateAsync(Guid id, UpdateEventHookRequest request, CancellationToken ct = default);
    Task<OperationResult> VerifyAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult> ReplayDeadLetterAsync(Guid deliveryId, string idempotencyKey, CancellationToken ct = default);
    Task<PagedResult<EventHookDeliveryDto>> GetDeliveriesAsync(EventHookDeliveryQuery query, CancellationToken ct = default);
    Task<EventHookDeliveryDto?> GetDeliveryAsync(Guid id, CancellationToken ct = default);

    /// <summary>Replaces the signing secret; the previous one keeps signing for a grace period.</summary>
    Task<OperationResult<EventHookSecretResponse>> RotateSecretAsync(Guid id, CancellationToken ct = default);

    IReadOnlyList<EventTypeDto> GetEventTypes();
}
