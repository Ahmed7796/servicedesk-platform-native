using ServiceDesk.Domain.Common;
using ServiceDesk.Domain.Enums;
using ServiceDesk.Domain.Exceptions;
using ServiceDesk.Domain.Workflow;

namespace ServiceDesk.Domain.Entities;

public sealed class ServiceRequest : Entity
{
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 4000;

    private readonly List<ApprovalStep> _approvalSteps = [];
    public IReadOnlyList<ApprovalStep> ApprovalSteps => _approvalSteps.AsReadOnly();

    private readonly List<AuditLog> _auditLogs = [];
    public IReadOnlyList<AuditLog> AuditLogs => _auditLogs.AsReadOnly();

    public string Title { get; private set; } = null!;
    public string Description { get; private set; } = null!;
    public RequestType Type { get; private set; }
    public RequestStatus Status { get; private set; } = RequestStatus.Submitted;
    public Guid RequesterId { get; private set; }
    public ApproverPosition? RequesterPosition { get; private set; } // Snapshot at submission, used for the Admin fallback.
    public Guid DepartmentId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint RowVersion { get; private set; } // PostgreSQL xmin, set by the database.
    public ApprovalStep? CurrentStep => _approvalSteps.Where(s => s.Decision == StepDecision.Pending).OrderBy(s => s.Order).FirstOrDefault();


    private ServiceRequest() { } // For Core EF

    public static ServiceRequest Submit(User requester, string title, string description, WorkflowTemplate workflow, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(workflow);

        var trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmedDescription == null)
            throw new ArgumentException("Description is required.", nameof(description));

        if (trimmedDescription.Length > DescriptionMaxLength)
            throw new ArgumentException($"Description can be only {DescriptionMaxLength} characters at most.", nameof(description));


        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title is required.", nameof(title));

        var trimmedTitle = title.Trim();
        if (trimmedTitle.Length > TitleMaxLength)
            throw new ArgumentException($"Title can be {TitleMaxLength} characters at most.", nameof(title));

        ServiceRequest request = new ServiceRequest();
        request.RequesterId = requester.Id;
        request.RequesterPosition = requester.IsApprover ? requester.Position : null;
        request.Title = trimmedTitle;
        request.Description = trimmedDescription;
        request.DepartmentId = requester.DepartmentId;
        request.Status = RequestStatus.Submitted;
        request.Type = workflow.RequestType;
        request.CreatedAt = now;
        request.UpdatedAt = now;

        request.AppendAudit(request.RequesterId, AuditAction.Submitted, null, RequestStatus.Submitted, "Submitted.", now);

        foreach (var step in workflow.Steps.OrderBy(s => s.Order))
        {
            var requesterHoldsStep = requester.IsApprover && requester.Position == step.Position;

            ApproverPosition? escalated = requesterHoldsStep ? EscalationPolicy.Escalate(step.Position) : null;

            if (escalated is not null)
            {
                request._approvalSteps.Add(new ApprovalStep(
                    request.Id,
                    step.Order,
                    escalated.Value,
                    ApprovalScope.Global,
                    escalatedFrom: step.Position));
                request.AppendAudit(
                    requester.Id,
                    AuditAction.StepEscalated,
                    RequestStatus.Submitted,
                    RequestStatus.Submitted,
                    $"Step {step.Order} escalated from {step.Position} to {escalated} (requester holds {step.Position})",
                    now
                );
            }
            else
            {
                request._approvalSteps.Add(new ApprovalStep(request.Id, step.Order, step.Position, step.Scope));
            }

        }
        return request;
    }

    public void Approve(User approver, string? comment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(approver);

        var step = EnsureCanDecideCurrentStep(approver, RequestTrigger.ApproveStep);

        var before = Status;
        step.Approve(approver.Id, comment, now);

        RequestTrigger trigger = CurrentStep == null ? RequestTrigger.ApproveFinalStep : RequestTrigger.ApproveStep;

        Status = RequestStateMachine.Fire(before, trigger);
        UpdatedAt = now;

        AppendAudit(
            approver.Id,
            AuditAction.StepApproved,
            before,
            Status,
            $"Step {step.Order} ({step.Position}) approved{OnBehalfNote(approver, step)}.",
            now);
    }

    public void Reject(User approver, string reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(approver);

        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to reject a request.", nameof(reason));

        var trimmedReason = reason.Trim();
        if (trimmedReason.Length > ApprovalStep.CommentMaxLength)
            throw new ArgumentException($"Reason can be {ApprovalStep.CommentMaxLength} characters at most.", nameof(reason));

        var step = EnsureCanDecideCurrentStep(approver, RequestTrigger.Reject);

        var before = Status;
        step.Reject(approver.Id, trimmedReason, now);
        SkipPendingSteps();

        Status = RequestStateMachine.Fire(before, RequestTrigger.Reject);
        UpdatedAt = now;

        AppendAudit(
            approver.Id,
            AuditAction.StepRejected,
            before,
            Status,
            $"Step {step.Order} ({step.Position}) rejected{OnBehalfNote(approver, step)}: {trimmedReason}",
            now);
    }

    public void Cancel(User requester, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(requester);

        if (!RequestStateMachine.CanFire(Status, RequestTrigger.Cancel))
            throw new InvalidStateTransitionException($"Cannot cancel a request that is {Status}");

        if (requester.Id != RequesterId)
            throw new ApprovalNotAllowedException("Only the original requester can cancel this request.");

        var before = Status;
        SkipPendingSteps();

        Status = RequestStateMachine.Fire(before, RequestTrigger.Cancel);
        UpdatedAt = now;

        AppendAudit(requester.Id, AuditAction.Cancelled, before, Status, "Cancelled by requester.", now);
    }

    public void Fulfil(User admin, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(admin);

        if (!RequestStateMachine.CanFire(Status, RequestTrigger.Fulfil))
            throw new InvalidStateTransitionException($"Cannot fulfil a request that is {Status}");

        if (admin.Role != UserRole.Admin)
            throw new ApprovalNotAllowedException("Only an Admin can fulfil a request.");

        var before = Status;
        Status = RequestStateMachine.Fire(before, RequestTrigger.Fulfil);
        UpdatedAt = now;

        AppendAudit(admin.Id, AuditAction.Fulfilled, before, Status, "Fulfilled.", now);
    }

    /// <summary>
    /// True when <paramref name="actor"/> may decide the current step right now. Shares its rule chain with
    /// <see cref="EnsureCanDecideCurrentStep"/>, so the two cannot disagree.
    /// </summary>
    public bool CanDecideCurrentStep(User actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return FindDecisionBlock(actor, RequestTrigger.ApproveStep, out _) == DecisionBlock.None;
    }

    // The first rule that stops the actor from deciding, or None. The order matters and is the order the checks always had.
    private enum DecisionBlock
    {
        None,
        InvalidState,
        NoPendingStep,
        IsRequester,
        WrongPosition,
        WrongDepartment,
    }

    private DecisionBlock FindDecisionBlock(User actor, RequestTrigger trigger, out ApprovalStep? step)
    {
        step = null;

        if (!RequestStateMachine.CanFire(Status, trigger))
            return DecisionBlock.InvalidState;

        step = CurrentStep;
        if (step is null)
            return DecisionBlock.NoPendingStep;

        if (RequesterId == actor.Id)
            return DecisionBlock.IsRequester;

        // Admin fallback: when the requester holds the step's position, Admins may also decide the step, in addition to the other holders of that position.
        if (IsAdminFallback(actor, step))
            return DecisionBlock.None;

        if (!actor.IsApprover || actor.Position != step.Position)
            return DecisionBlock.WrongPosition;

        if (step.Scope == ApprovalScope.RequesterDepartment && actor.DepartmentId != DepartmentId)
            return DecisionBlock.WrongDepartment;

        return DecisionBlock.None;
    }

    private ApprovalStep EnsureCanDecideCurrentStep(User actor, RequestTrigger trigger)
    {
        var verb = trigger == RequestTrigger.Reject ? "reject" : "approve";

        var block = FindDecisionBlock(actor, trigger, out var step);

        return block switch
        {
            DecisionBlock.None => step!,
            DecisionBlock.InvalidState => throw new InvalidStateTransitionException($"Cannot {verb} a request that is {Status}"),
            DecisionBlock.NoPendingStep => throw new InvalidStateTransitionException("No approval step is awaiting a decision."),
            DecisionBlock.IsRequester => throw new ApprovalNotAllowedException($"You cannot {verb} your own request."),
            DecisionBlock.WrongPosition => throw new ApprovalNotAllowedException($"Only {step!.Position} is authorized to {verb} step {step.Order}."),
            DecisionBlock.WrongDepartment => throw new ApprovalNotAllowedException($"Step {step!.Order} must only be {verb}d by a {step.Position} in the requester's department."),
            _ => throw new InvalidOperationException($"Unhandled decision block {block}."),
        };
    }

    private bool IsAdminFallback(User actor, ApprovalStep step) =>
        actor.Role == UserRole.Admin && RequesterPosition == step.Position;

    private string OnBehalfNote(User actor, ApprovalStep step) =>
        IsAdminFallback(actor, step) ? $", decided by Admin on behalf of {step.Position}" : string.Empty;

    // The only place an audit entry is created. Sequence is 1, 2, 3... per request, so the trail has a stable order
    // even when entries share a timestamp. It relies on _auditLogs holding every existing entry, so any load that
    // leads to a state change must Include(AuditLogs); a unique index (ServiceRequestId, Sequence) is the backstop.
    private void AppendAudit(Guid actorId, AuditAction action, RequestStatus? before, RequestStatus after, string? details, DateTimeOffset now)
    {
        _auditLogs.Add(new AuditLog(Id, actorId, _auditLogs.Count + 1, action, before, after, details, now));
    }

    private void SkipPendingSteps()
    {
        foreach (var pending in _approvalSteps.Where(s => s.Decision == StepDecision.Pending))
            pending.Skip();
    }
}
