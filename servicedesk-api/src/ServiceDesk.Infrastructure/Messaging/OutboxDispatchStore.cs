using Microsoft.EntityFrameworkCore;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Infrastructure.Persistence;

namespace ServiceDesk.Infrastructure.Messaging;

public sealed class OutboxDispatchStore(AppDbContext db) : IOutboxDispatchStore
{
    // Uses the filtered index on OccurredAt WHERE "SentAt" IS NULL.
    public async Task<IReadOnlyList<OutboxMessage>> GetUnsentAsync(int limit, CancellationToken ct) =>
        await db.OutboxMessages
            .Where(m => m.SentAt == null)
            .OrderBy(m => m.OccurredAt).ThenBy(m => m.Id)
            .Take(limit)
            .ToListAsync(ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
