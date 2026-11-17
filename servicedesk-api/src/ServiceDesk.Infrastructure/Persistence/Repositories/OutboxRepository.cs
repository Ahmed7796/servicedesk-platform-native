using ServiceDesk.Application.Abstractions;
using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Infrastructure.Persistence.Repositories;

public sealed class OutboxRepository(AppDbContext db) : IOutboxRepository
{
    public void Add(OutboxMessage message) => db.OutboxMessages.Add(message);
}
