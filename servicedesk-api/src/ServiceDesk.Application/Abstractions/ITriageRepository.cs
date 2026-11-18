using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Application.Abstractions;

public interface ITriageRepository
{
    // Insert or replace the one suggestion of this request, in a single statement. Does not use IUnitOfWork.
    Task UpsertAsync(TriageSuggestion suggestion, CancellationToken ct);

    Task<TriageSuggestion?> GetByRequestIdAsync(Guid requestId, CancellationToken ct);

    // One query for many requests (no N+1). Requests without a suggestion are simply missing from the result.
    Task<IReadOnlyList<TriageSuggestion>> ListByRequestIdsAsync(IEnumerable<Guid> requestIds, CancellationToken ct);
}
