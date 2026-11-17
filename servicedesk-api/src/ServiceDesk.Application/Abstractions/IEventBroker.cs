using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Application.Abstractions;

/// <summary>
/// Publishes one outbox row to the message broker. The task completes only when the broker has confirmed
/// the message (publisher confirms). The routing key is the row's Type; the body is sent as stored.
/// </summary>
public interface IEventBroker
{
    /// <exception cref="BrokerUnavailableException">The broker cannot be reached; retry later.</exception>
    /// <exception cref="PublishRejectedException">The broker refused this message; other messages may still work.</exception>
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}

/// <summary>Connection, channel or confirm timeout. The publisher stops the batch and backs off.</summary>
public sealed class BrokerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The broker nacked this one message. The publisher skips the row for now and retries it next batch.</summary>
public sealed class PublishRejectedException(string message, Exception? inner = null) : Exception(message, inner);
