using System.Text.Json;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Application.Events;

/// <summary>
/// Builds the outbox row for each state change. Pure: no database, no clock. The caller passes the request AFTER the
/// transition, the actor, the transition's <c>now</c> and (for two events) the approver ids it resolved.
/// The row's Id is generated here, once, and is the envelope's eventId.
/// </summary>
public static class OutboxEventFactory
{
    private const int Version = 1;

    // Property names are camelCase. Enums never reach the serializer: the payload records hold strings made with .ToString().
    // Never ignoring nulls is the default; it is stated on purpose so comment/escalatedFrom are always written as null.
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static OutboxMessage Submitted(ServiceRequest request, Guid actorId, DateTimeOffset now, IReadOnlyList<Guid> currentStepApprovers)
    {
        var payload = new SubmittedPayload(
            request.RequesterId,
            request.Title,
            request.Type.ToString(),
            request.DepartmentId,
            request.Status.ToString(),
            request.ApprovalSteps.OrderBy(s => s.Order).Select(ToStep).ToList(),
            ToActionable(RequireCurrentStep(request), currentStepApprovers));

        return Build("request.submitted", request, actorId, now, payload);
    }

    /// <summary>Call after Approve when a later step is pending. <paramref name="decidedStep"/> is the step that was current before Approve.</summary>
    public static OutboxMessage StepApproved(ServiceRequest request, ApprovalStep decidedStep, Guid actorId, DateTimeOffset now, IReadOnlyList<Guid> nextStepApprovers)
    {
        var payload = new StepApprovedPayload(
            request.RequesterId,
            ToDecided(decidedStep),
            decidedStep.Comment, // already trimmed; blank is stored as null, so it is never ""
            request.Status.ToString(),
            ToActionable(RequireCurrentStep(request), nextStepApprovers));

        return Build("request.step_approved", request, actorId, now, payload);
    }

    public static OutboxMessage Approved(ServiceRequest request, ApprovalStep decidedStep, Guid actorId, DateTimeOffset now)
    {
        var payload = new ApprovedPayload(request.RequesterId, ToDecided(decidedStep), decidedStep.Comment, request.Status.ToString());

        return Build("request.approved", request, actorId, now, payload);
    }

    public static OutboxMessage Rejected(ServiceRequest request, ApprovalStep decidedStep, Guid actorId, DateTimeOffset now)
    {
        // Reject requires a reason, and the step stores it trimmed in Comment.
        var payload = new RejectedPayload(request.RequesterId, ToDecided(decidedStep), decidedStep.Comment ?? throw new InvalidOperationException("A rejected step has no reason."), request.Status.ToString());

        return Build("request.rejected", request, actorId, now, payload);
    }

    public static OutboxMessage Cancelled(ServiceRequest request, RequestStatus previousStatus, Guid actorId, DateTimeOffset now)
    {
        var payload = new CancelledPayload(request.RequesterId, previousStatus.ToString(), request.Status.ToString());

        return Build("request.cancelled", request, actorId, now, payload);
    }

    public static OutboxMessage Fulfilled(ServiceRequest request, Guid actorId, DateTimeOffset now)
    {
        var payload = new FulfilledPayload(request.RequesterId, request.Status.ToString());

        return Build("request.fulfilled", request, actorId, now, payload);
    }

    private static OutboxMessage Build<TPayload>(string type, ServiceRequest request, Guid actorId, DateTimeOffset now, TPayload payload)
    {
        var eventId = Guid.CreateVersion7();
        var envelope = new EventEnvelope<TPayload>(eventId, type, now.UtcDateTime, request.Id, actorId, payload, Version);

        return new OutboxMessage(eventId, type, request.Id, now, JsonSerializer.Serialize(envelope, JsonOptions));
    }

    private static ApprovalStep RequireCurrentStep(ServiceRequest request) =>
        request.CurrentStep ?? throw new InvalidOperationException("The request has no pending step.");

    private static StepPayload ToStep(ApprovalStep s) => new(s.Order, s.Position.ToString(), s.Scope.ToString(), s.EscalatedFrom?.ToString());

    private static ActionableStepPayload ToActionable(ApprovalStep s, IReadOnlyList<Guid> approvers) =>
        new(s.Order, s.Position.ToString(), s.Scope.ToString(), s.EscalatedFrom?.ToString(), approvers);

    private static DecidedStepPayload ToDecided(ApprovalStep s) => new(s.Order, s.Position.ToString(), s.Scope.ToString());
}
