using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.DTOs;
using ServiceDesk.Application.Events;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;
using ServiceDesk.Domain.Exceptions;

namespace ServiceDesk.Application.Services;

public sealed class ServiceRequestService(IServiceRequestRepository requests, IUserRepository users, IWorkflowTemplateRepository workflows, IUnitOfWork unitOfWork, IOutboxRepository outbox, ApproverResolver approvers, TimeProvider timeProvider)
{
    public async Task<ServiceRequestDto> SubmitAsync(Guid requesterId, SubmitServiceRequestDto dto, CancellationToken ct)
    {
        var requester = await users.GetByIdAsync(requesterId, ct) ?? throw new NotFoundException($"User {requesterId} not found.");

        var workflow = await workflows.GetByTypeAsync(dto.Type, ct) ?? throw new NotFoundException($"No workflow is configured for {dto.Type} requests.");

        var now = timeProvider.GetUtcNow();
        var request = ServiceRequest.Submit(requester, dto.Title, dto.Description, workflow, now);

        // Resolve before any Add, so a resolver failure stages nothing.
        var approverIds = await approvers.GetEligibleApproversAsync(request, ct);
        requests.Add(request);
        outbox.Add(OutboxEventFactory.Submitted(request, requester.Id, now, approverIds));

        await unitOfWork.SaveChangesAsync(ct);

        return ServiceRequestDto.FromEntity(request);
    }

    public async Task<ServiceRequestDto> GetByIdAsync(Guid requestId, CancellationToken ct)
    {
        var request = await requests.GetByIdAsync(requestId, ct) ?? throw new NotFoundException($"Request {requestId} not found.");

        return ServiceRequestDto.FromEntity(request);
    }

    public async Task<IReadOnlyList<ServiceRequestDto>> ListAsync(CancellationToken ct)
    {
        var all = await requests.ListAsync(ct);

        return all.Select(ServiceRequestDto.FromEntity).ToList();
    }

    public async Task<IReadOnlyList<PendingItemDto>> ListPendingForAsync(Guid callerId, CancellationToken ct)
    {
        var caller = await users.GetByIdAsync(callerId, ct) ?? throw new NotFoundException($"User {callerId} not found.");

        // Shortcut only: a plain Requester can never decide a step, so skip the query.
        if (caller.Role != UserRole.Admin && !caller.IsApprover)
            return [];

        var candidates = await requests.ListPendingCandidatesAsync(caller, ct);
        var eligible = candidates
            .Where(r => r.CanDecideCurrentStep(caller)) // the one eligibility rule
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToList();
        if (eligible.Count == 0)
            return [];

        // One query for all requester names (no N+1).
        var requesters = await users.ListByIdsAsync(eligible.Select(r => r.RequesterId).Distinct(), ct);
        var names = requesters.ToDictionary(u => u.Id, u => u.DisplayName);

        return eligible.Select(r => PendingItemDto.From(r, names[r.RequesterId])).ToList();
    }

    public async Task<ServiceRequestDto> ApproveAsync(Guid requestId, Guid approverId, string? comment, CancellationToken ct)
    {
        var request = await requests.GetByIdAsync(requestId, ct) ?? throw new NotFoundException($"Request {requestId} not found.");

        var approver = await users.GetByIdAsync(approverId, ct) ?? throw new NotFoundException($"User {approverId} not found.");

        var now = timeProvider.GetUtcNow();
        var decidedStep = request.CurrentStep!; // captured BEFORE the transition; Approve passes its checks only when a step is pending
        request.Approve(approver, comment, now);

        if (request.Status == RequestStatus.Approved)
        {
            outbox.Add(OutboxEventFactory.Approved(request, decidedStep, approver.Id, now));
        }
        else
        {
            var nextApprovers = await approvers.GetEligibleApproversAsync(request, ct); // next step, so AFTER the approve
            outbox.Add(OutboxEventFactory.StepApproved(request, decidedStep, approver.Id, now, nextApprovers));
        }

        await unitOfWork.SaveChangesAsync(ct);

        return ServiceRequestDto.FromEntity(request);
    }

    public async Task<ServiceRequestDto> RejectAsync(Guid requestId, Guid approverId, string reason, CancellationToken ct)
    {
        var request = await requests.GetByIdAsync(requestId, ct) ?? throw new NotFoundException($"Request {requestId} not found.");

        var approver = await users.GetByIdAsync(approverId, ct) ?? throw new NotFoundException($"User {approverId} not found.");

        var now = timeProvider.GetUtcNow();
        var decidedStep = request.CurrentStep!; // captured BEFORE the transition
        request.Reject(approver, reason, now);
        outbox.Add(OutboxEventFactory.Rejected(request, decidedStep, approver.Id, now));

        await unitOfWork.SaveChangesAsync(ct);

        return ServiceRequestDto.FromEntity(request);
    }

    public async Task<ServiceRequestDto> CancelAsync(Guid requestId, Guid requesterId, CancellationToken ct)
    {
        var request = await requests.GetByIdAsync(requestId, ct) ?? throw new NotFoundException($"Request {requestId} not found.");

        var requester = await users.GetByIdAsync(requesterId, ct) ?? throw new NotFoundException($"User {requesterId} not found.");

        var now = timeProvider.GetUtcNow();
        var previousStatus = request.Status; // captured BEFORE; Cancel throws unless this is Submitted or InReview
        request.Cancel(requester, now);
        outbox.Add(OutboxEventFactory.Cancelled(request, previousStatus, requester.Id, now));

        await unitOfWork.SaveChangesAsync(ct);

        return ServiceRequestDto.FromEntity(request);
    }

    public async Task<ServiceRequestDto> FulfilAsync(Guid requestId, Guid adminId, CancellationToken ct)
    {
        var request = await requests.GetByIdAsync(requestId, ct) ?? throw new NotFoundException($"Request {requestId} not found.");

        var admin = await users.GetByIdAsync(adminId, ct) ?? throw new NotFoundException($"User {adminId} not found.");

        var now = timeProvider.GetUtcNow();
        request.Fulfil(admin, now);
        outbox.Add(OutboxEventFactory.Fulfilled(request, admin.Id, now));

        await unitOfWork.SaveChangesAsync(ct);

        return ServiceRequestDto.FromEntity(request);
    }
}
