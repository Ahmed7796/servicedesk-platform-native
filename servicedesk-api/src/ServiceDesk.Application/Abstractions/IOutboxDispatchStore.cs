using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Application.Abstractions;

/// <summary>The publisher's view of the outbox table. Rows come back tracked, so SaveAsync writes their SentAt.</summary>
public interface IOutboxDispatchStore
{
    /// <summary>Unsent rows, oldest first (OccurredAt, then Id), at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<OutboxMessage>> GetUnsentAsync(int limit, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
}
