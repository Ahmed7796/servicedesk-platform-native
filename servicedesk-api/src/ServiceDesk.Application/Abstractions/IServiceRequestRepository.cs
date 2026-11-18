using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Application.Abstractions;

public interface IServiceRequestRepository
{
    Task<ServiceRequest?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<ServiceRequest>> ListAsync(CancellationToken ct);

    // A superset of the requests the caller may decide (SQL pre-filter). The domain rule
    // ServiceRequest.CanDecideCurrentStep does the real filtering. Includes ApprovalSteps, read-only.
    Task<IReadOnlyList<ServiceRequest>> ListPendingCandidatesAsync(User caller, CancellationToken ct);

    // Existence check only; loads nothing into the change tracker.
    Task<bool> ExistsAsync(Guid id, CancellationToken ct);

    void Add(ServiceRequest request);

}
