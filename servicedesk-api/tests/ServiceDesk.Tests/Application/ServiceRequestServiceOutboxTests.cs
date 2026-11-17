using System.Text.Json;
using NSubstitute;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.DTOs;
using ServiceDesk.Application.Services;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;
using ServiceDesk.Domain.Exceptions;

namespace ServiceDesk.Tests.Application;

// M1-1b: every state change adds exactly one outbox row before the one SaveChanges, and a failed change adds none.
public class ServiceRequestServiceOutboxTests
{
    private static readonly Guid OpsDeptId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly IServiceRequestRepository _requests = Substitute.For<IServiceRequestRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IWorkflowTemplateRepository _workflows = Substitute.For<IWorkflowTemplateRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly TimeProvider _timeProvider = Substitute.For<TimeProvider>();

    private readonly List<OutboxMessage> _added = [];
    private readonly List<string> _calls = [];

    private readonly User _requester = User.CreateRequester("requester@mail.com", "1001", "hash", "Omar", OpsDeptId);
    private readonly User _opsHead = User.CreateApprover("opshead@mail.com", "2001", "hash", "Layla", OpsDeptId, ApproverPosition.DepartmentHead);
    private readonly User _itManager = User.CreateApprover("it@mail.com", "3001", "hash", "Sara", OpsDeptId, ApproverPosition.ItManager);
    private readonly User _gm = User.CreateApprover("gm@mail.com", "4001", "hash", "Yousef", OpsDeptId, ApproverPosition.GeneralManager);
    private readonly User _admin = User.CreateAdmin("admin@mail.com", "9001", "hash", "Admin", OpsDeptId);
    private readonly List<User> _all;

    private readonly WorkflowTemplate _twoSteps = new(RequestType.Equipment, "Equipment request", [(ApproverPosition.DepartmentHead, ApprovalScope.RequesterDepartment), (ApproverPosition.ItManager, ApprovalScope.Global)]);
    private readonly WorkflowTemplate _oneStep = new(RequestType.SoftwareAccess, "Software access request", [(ApproverPosition.ItManager, ApprovalScope.Global)]);
    private readonly WorkflowTemplate _gmOnly = new(RequestType.Equipment, "GM sign-off", [(ApproverPosition.GeneralManager, ApprovalScope.Global)]);

    private readonly ServiceRequestService _service;

    public ServiceRequestServiceOutboxTests()
    {
        _all = [_requester, _opsHead, _itManager, _gm, _admin];
        _timeProvider.GetUtcNow().Returns(Now);
        foreach (var user in _all)
            _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        _users.ListApproverCandidatesAsync(Arg.Any<ApproverPosition>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<User>>(
                _all.Where(u => u.Role == UserRole.Admin || (u.Role == UserRole.Approver && u.Position == call.Arg<ApproverPosition>())).ToList()));
        _workflows.GetByTypeAsync(RequestType.Equipment, Arg.Any<CancellationToken>()).Returns(_twoSteps);

        _outbox.When(o => o.Add(Arg.Any<OutboxMessage>())).Do(c => { _added.Add(c.Arg<OutboxMessage>()); _calls.Add("add"); });
        _unitOfWork.When(u => u.SaveChangesAsync(Arg.Any<CancellationToken>())).Do(_ => _calls.Add("save"));

        _service = new ServiceRequestService(_requests, _users, _workflows, _unitOfWork, _outbox, new ApproverResolver(_users), _timeProvider);
    }

    private ServiceRequest Load(User requester, WorkflowTemplate workflow)
    {
        var request = ServiceRequest.Submit(requester, "New laptop", "Current one is old", workflow, Now);
        _requests.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        return request;
    }

    private ServiceRequest LoadInState(RequestStatus status)
    {
        var request = Load(_requester, _oneStep);
        if (status == RequestStatus.Rejected) request.Reject(_itManager, "No.", Now);
        if (status == RequestStatus.Cancelled) request.Cancel(_requester, Now);
        if (status is RequestStatus.Approved or RequestStatus.Fulfilled) request.Approve(_itManager, null, Now);
        if (status == RequestStatus.Fulfilled) request.Fulfil(_admin, Now);
        return request;
    }

    private static JsonElement Root(OutboxMessage m) => JsonDocument.Parse(m.Body).RootElement;

    public enum Case { Submit, SubmitEscalated, ApproveNotLast, ApproveLast, Reject, CancelFromSubmitted, CancelFromInReview, Fulfil, AdminApprovesForGm }

    [Theory]
    [InlineData(Case.Submit, "request.submitted")]
    [InlineData(Case.SubmitEscalated, "request.submitted")]
    [InlineData(Case.ApproveNotLast, "request.step_approved")]
    [InlineData(Case.ApproveLast, "request.approved")]
    [InlineData(Case.Reject, "request.rejected")]
    [InlineData(Case.CancelFromSubmitted, "request.cancelled")]
    [InlineData(Case.CancelFromInReview, "request.cancelled")]
    [InlineData(Case.Fulfil, "request.fulfilled")]
    [InlineData(Case.AdminApprovesForGm, "request.approved")]
    public async Task Transition_AddsExactlyOneOutboxRow_BeforeTheOneSave(Case which, string expectedType)
    {
        var ct = CancellationToken.None;
        Guid actor, requestId;
        switch (which)
        {
            case Case.Submit:
                actor = _requester.Id;
                requestId = (await _service.SubmitAsync(actor, new SubmitServiceRequestDto(RequestType.Equipment, "T", "D"), ct)).Id;
                break;
            case Case.SubmitEscalated: // the department head's own request: step 1 escalates to GeneralManager
                actor = _opsHead.Id;
                requestId = (await _service.SubmitAsync(actor, new SubmitServiceRequestDto(RequestType.Equipment, "T", "D"), ct)).Id;
                break;
            case Case.ApproveNotLast:
                actor = _opsHead.Id;
                requestId = (await _service.ApproveAsync(Load(_requester, _twoSteps).Id, actor, "ok", ct)).Id;
                break;
            case Case.ApproveLast:
                actor = _itManager.Id;
                requestId = (await _service.ApproveAsync(Load(_requester, _oneStep).Id, actor, null, ct)).Id;
                break;
            case Case.Reject:
                actor = _opsHead.Id;
                requestId = (await _service.RejectAsync(Load(_requester, _twoSteps).Id, actor, "No budget", ct)).Id;
                break;
            case Case.CancelFromSubmitted:
                actor = _requester.Id;
                requestId = (await _service.CancelAsync(Load(_requester, _twoSteps).Id, actor, ct)).Id;
                break;
            case Case.CancelFromInReview:
                actor = _requester.Id;
                var inReview = Load(_requester, _twoSteps);
                inReview.Approve(_opsHead, null, Now);
                requestId = (await _service.CancelAsync(inReview.Id, actor, ct)).Id;
                break;
            case Case.Fulfil:
                actor = _admin.Id;
                requestId = (await _service.FulfilAsync(LoadInState(RequestStatus.Approved).Id, actor, ct)).Id;
                break;
            default: // a GeneralManager's own request: the Admin decides the GM step on the GM's behalf
                actor = _admin.Id;
                requestId = (await _service.ApproveAsync(Load(_gm, _gmOnly).Id, actor, null, ct)).Id;
                break;
        }

        var row = Assert.Single(_added);
        Assert.Equal(expectedType, row.Type);
        Assert.Equal(requestId, row.RequestId);
        Assert.Equal(actor, Root(row).GetProperty("actorId").GetGuid());
        Assert.Equal(["add", "save"], _calls);
        if (which == Case.SubmitEscalated)
            Assert.Equal("DepartmentHead", Root(row).GetProperty("payload").GetProperty("currentStep").GetProperty("escalatedFrom").GetString());
        if (which == Case.CancelFromInReview)
            Assert.Equal("InReview", Root(row).GetProperty("payload").GetProperty("previousStatus").GetString());
        if (which == Case.CancelFromSubmitted)
            Assert.Equal("Submitted", Root(row).GetProperty("payload").GetProperty("previousStatus").GetString());
    }

    [Fact]
    public async Task ApproveNotLast_ResolvesApproversOfTheNextStep_AfterTheApprove()
    {
        var request = Load(_requester, _twoSteps);

        await _service.ApproveAsync(request.Id, _opsHead.Id, null, CancellationToken.None);

        var payload = Root(Assert.Single(_added)).GetProperty("payload");
        Assert.Equal(1, payload.GetProperty("decidedStep").GetProperty("order").GetInt32());
        Assert.Equal("ItManager", payload.GetProperty("nextStep").GetProperty("position").GetString());
        Assert.Equal([_itManager.Id], payload.GetProperty("nextStep").GetProperty("approverUserIds").EnumerateArray().Select(e => e.GetGuid()).ToList()); // not the department head, not the Admin (no fallback here)
    }

    // Cannot catch a resolver call on the final approve (the resolver returns [] early when no step is pending: an equivalent mutant); it guards only against a regression in the resolver itself.
    [Fact]
    public async Task ApproveLast_DoesNotCallTheResolver()
    {
        var request = Load(_requester, _oneStep);
        _users.ClearReceivedCalls();

        await _service.ApproveAsync(request.Id, _itManager.Id, null, CancellationToken.None);

        await _users.DidNotReceive().ListApproverCandidatesAsync(Arg.Any<ApproverPosition>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_ResolverReturnsDuplicates_EventHasUniqueApproverIds()
    {
        _users.ListApproverCandidatesAsync(Arg.Any<ApproverPosition>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<User>>([_opsHead, _opsHead]));

        await _service.SubmitAsync(_requester.Id, new SubmitServiceRequestDto(RequestType.Equipment, "T", "D"), CancellationToken.None);

        var ids = Root(Assert.Single(_added)).GetProperty("payload").GetProperty("currentStep").GetProperty("approverUserIds").EnumerateArray().Select(e => e.GetGuid()).ToList();
        Assert.Equal([_opsHead.Id], ids);
    }

    public enum Fail
    {
        SubmitUserMissing, SubmitWorkflowMissing, ApproveRequestMissing, ApproveUserMissing, RejectRequestMissing, CancelRequestMissing, FulfilRequestMissing,
        ApproveWrongPosition, ApproveCancelled, RejectBlankReason, CancelByOtherUser, FulfilByNonAdmin, FulfilWhileSubmitted,
        CancelApproved, CancelRejected, CancelCancelled, CancelFulfilled,
        SubmitResolverThrows, ApproveResolverThrows,
    }

    [Theory]
    [InlineData(Fail.SubmitUserMissing, typeof(NotFoundException))]
    [InlineData(Fail.SubmitWorkflowMissing, typeof(NotFoundException))]
    [InlineData(Fail.ApproveRequestMissing, typeof(NotFoundException))]
    [InlineData(Fail.ApproveUserMissing, typeof(NotFoundException))]
    [InlineData(Fail.RejectRequestMissing, typeof(NotFoundException))]
    [InlineData(Fail.CancelRequestMissing, typeof(NotFoundException))]
    [InlineData(Fail.FulfilRequestMissing, typeof(NotFoundException))]
    [InlineData(Fail.ApproveWrongPosition, typeof(ApprovalNotAllowedException))]
    [InlineData(Fail.ApproveCancelled, typeof(InvalidStateTransitionException))]
    [InlineData(Fail.RejectBlankReason, typeof(ArgumentException))]
    [InlineData(Fail.CancelByOtherUser, typeof(ApprovalNotAllowedException))]
    [InlineData(Fail.FulfilByNonAdmin, typeof(ApprovalNotAllowedException))]
    [InlineData(Fail.FulfilWhileSubmitted, typeof(InvalidStateTransitionException))]
    [InlineData(Fail.CancelApproved, typeof(InvalidStateTransitionException))] // Cancel is allowed only from Submitted and InReview, which is the whole
    [InlineData(Fail.CancelRejected, typeof(InvalidStateTransitionException))] // enum of request.cancelled.previousStatus, so no other status
    [InlineData(Fail.CancelCancelled, typeof(InvalidStateTransitionException))] // can reach OutboxEventFactory.Cancelled.
    [InlineData(Fail.CancelFulfilled, typeof(InvalidStateTransitionException))]
    [InlineData(Fail.SubmitResolverThrows, typeof(TimeoutException))]
    [InlineData(Fail.ApproveResolverThrows, typeof(TimeoutException))]
    public async Task Failure_AddsNoOutboxRow_AndDoesNotSave(Fail which, Type expected)
    {
        var ct = CancellationToken.None;
        var missing = Guid.NewGuid();
        var submit = new SubmitServiceRequestDto(RequestType.Equipment, "T", "D");
        Func<Task> act;
        switch (which)
        {
            case Fail.SubmitUserMissing: act = () => _service.SubmitAsync(missing, submit, ct); break;
            case Fail.SubmitWorkflowMissing: act = () => _service.SubmitAsync(_requester.Id, new SubmitServiceRequestDto(RequestType.Leave, "T", "D"), ct); break;
            case Fail.ApproveRequestMissing: act = () => _service.ApproveAsync(missing, _opsHead.Id, null, ct); break;
            case Fail.ApproveUserMissing: act = () => _service.ApproveAsync(Load(_requester, _twoSteps).Id, missing, null, ct); break;
            case Fail.RejectRequestMissing: act = () => _service.RejectAsync(missing, _opsHead.Id, "No", ct); break;
            case Fail.CancelRequestMissing: act = () => _service.CancelAsync(missing, _requester.Id, ct); break;
            case Fail.FulfilRequestMissing: act = () => _service.FulfilAsync(missing, _admin.Id, ct); break;
            case Fail.ApproveWrongPosition: act = () => _service.ApproveAsync(Load(_requester, _twoSteps).Id, _itManager.Id, null, ct); break;
            case Fail.ApproveCancelled: act = () => _service.ApproveAsync(LoadInState(RequestStatus.Cancelled).Id, _itManager.Id, null, ct); break;
            case Fail.RejectBlankReason: act = () => _service.RejectAsync(Load(_requester, _twoSteps).Id, _opsHead.Id, "  ", ct); break;
            case Fail.CancelByOtherUser: act = () => _service.CancelAsync(Load(_requester, _twoSteps).Id, _opsHead.Id, ct); break;
            case Fail.FulfilByNonAdmin: act = () => _service.FulfilAsync(LoadInState(RequestStatus.Approved).Id, _opsHead.Id, ct); break;
            case Fail.FulfilWhileSubmitted: act = () => _service.FulfilAsync(Load(_requester, _twoSteps).Id, _admin.Id, ct); break;
            case Fail.CancelApproved: act = () => _service.CancelAsync(LoadInState(RequestStatus.Approved).Id, _requester.Id, ct); break;
            case Fail.CancelRejected: act = () => _service.CancelAsync(LoadInState(RequestStatus.Rejected).Id, _requester.Id, ct); break;
            case Fail.CancelCancelled: act = () => _service.CancelAsync(LoadInState(RequestStatus.Cancelled).Id, _requester.Id, ct); break;
            case Fail.CancelFulfilled: act = () => _service.CancelAsync(LoadInState(RequestStatus.Fulfilled).Id, _requester.Id, ct); break;
            case Fail.SubmitResolverThrows:
                ResolverThrows();
                act = () => _service.SubmitAsync(_requester.Id, submit, ct);
                break;
            default:
                var request = Load(_requester, _twoSteps);
                ResolverThrows();
                act = () => _service.ApproveAsync(request.Id, _opsHead.Id, null, ct);
                break;
        }

        var ex = await Record.ExceptionAsync(act);

        Assert.IsType(expected, ex);
        _outbox.DidNotReceive().Add(Arg.Any<OutboxMessage>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        if (which == Fail.SubmitResolverThrows)
            _requests.DidNotReceive().Add(Arg.Any<ServiceRequest>());
    }

    private void ResolverThrows() =>
        _users.ListApproverCandidatesAsync(Arg.Any<ApproverPosition>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<User>>(new TimeoutException("database unavailable")));

    // A unit test cannot show the rollback: that is the database's job. This shows only that the row was added once and the exception reaches the caller.
    [Fact]
    public async Task Save_ConcurrencyConflict_PropagatesAfterOneAdd()
    {
        var request = Load(_requester, _twoSteps);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new ConcurrencyConflictException("conflict")));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => _service.ApproveAsync(request.Id, _opsHead.Id, null, CancellationToken.None));

        _outbox.Received(1).Add(Arg.Any<OutboxMessage>());
    }
}
