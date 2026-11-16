namespace ServiceDesk.Application.Events;

// Shapes of the event JSON in contracts/events/. Property names become camelCase when serialised.
// Enum-valued fields are plain strings here: the factory calls .ToString() on each enum, so the JSON holds the C# names ("DepartmentHead").

public sealed record EventEnvelope<TPayload>(
    Guid EventId,
    string Type,
    DateTime OccurredAt, // UTC, so it is written with a trailing Z
    Guid RequestId,
    Guid ActorId,
    TPayload Payload,
    int Version);

public sealed record StepPayload(int Order, string Position, string Scope, string? EscalatedFrom);

public sealed record ActionableStepPayload(int Order, string Position, string Scope, string? EscalatedFrom, IReadOnlyList<Guid> ApproverUserIds);

public sealed record DecidedStepPayload(int Order, string Position, string Scope);

public sealed record SubmittedPayload(
    Guid RequesterId,
    string Title,
    string RequestType,
    Guid DepartmentId,
    string Status,
    IReadOnlyList<StepPayload> Steps,
    ActionableStepPayload CurrentStep);

public sealed record StepApprovedPayload(Guid RequesterId, DecidedStepPayload DecidedStep, string? Comment, string Status, ActionableStepPayload NextStep);

public sealed record ApprovedPayload(Guid RequesterId, DecidedStepPayload DecidedStep, string? Comment, string Status);

public sealed record RejectedPayload(Guid RequesterId, DecidedStepPayload DecidedStep, string Reason, string Status);

public sealed record CancelledPayload(Guid RequesterId, string PreviousStatus, string Status);

public sealed record FulfilledPayload(Guid RequesterId, string Status);
