using ServiceDesk.Application.Abstractions;
using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Application.Services;

/// <summary>What one batch did. The caller logs and decides how long to wait.</summary>
public sealed record DispatchResult(int Published, IReadOnlyList<OutboxMessage> Rejected, bool BrokerUnavailable);

/// <summary>
/// Sends one batch of unsent outbox rows to the broker. A row is marked sent and saved only after the broker
/// confirmed it, so a crash in between re-sends the row (at-least-once; consumers de-duplicate on eventId).
/// </summary>
public sealed class OutboxDispatcher(IOutboxDispatchStore store, IEventBroker broker, TimeProvider clock)
{
    public async Task<DispatchResult> DispatchBatchAsync(int batchSize, CancellationToken ct)
    {
        var rows = await store.GetUnsentAsync(batchSize, ct);
        var published = 0;
        var rejected = new List<OutboxMessage>();

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await broker.PublishAsync(row, ct);
            }
            catch (BrokerUnavailableException)
            {
                // The broker is down: later rows would fail too. Leave them all unsent.
                return new DispatchResult(published, rejected, BrokerUnavailable: true);
            }
            catch (PublishRejectedException)
            {
                // One bad row must not block the rows behind it. It stays unsent and is retried next batch.
                rejected.Add(row);
                continue;
            }

            row.MarkSent(clock.GetUtcNow());
            // CancellationToken.None on purpose: the broker already confirmed, so a shutdown must not
            // skip this save and cause a needless duplicate.
            await store.SaveAsync(CancellationToken.None);
            published++;
        }

        return new DispatchResult(published, rejected, BrokerUnavailable: false);
    }
}
