using ServiceDesk.Domain.Common;

namespace ServiceDesk.Domain.Entities;

/// <summary>
/// One event waiting to be published to RabbitMQ. Id is the eventId inside the envelope.
/// Body is the full envelope JSON, sent as-is by the publisher (M1-2).
/// </summary>
public sealed class OutboxMessage : Entity
{
    public const int TypeMaxLength = 100;

    public string Type { get; private set; } = null!;
    public Guid RequestId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string Body { get; private set; } = null!;
    public DateTimeOffset? SentAt { get; private set; }

    private OutboxMessage() { } // For EF Core

    public OutboxMessage(Guid eventId, string type, Guid requestId, DateTimeOffset occurredAt, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        Id = eventId;
        Type = type;
        RequestId = requestId;
        OccurredAt = occurredAt;
        Body = body;
    }

    public void MarkSent(DateTimeOffset at) => SentAt = at;
}
