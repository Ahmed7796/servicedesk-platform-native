using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Application.Abstractions;

/// <summary>
/// Stages an outbox row. Nothing is written until <see cref="IUnitOfWork.SaveChangesAsync"/>, which saves the
/// row in the same transaction as the request change and its audit entry.
/// </summary>
public interface IOutboxRepository
{
    void Add(OutboxMessage message);
}
