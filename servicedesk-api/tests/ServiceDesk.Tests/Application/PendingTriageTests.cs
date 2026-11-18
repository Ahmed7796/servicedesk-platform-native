using NSubstitute;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Services;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Tests.Application;

public class PendingTriageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _dept = Guid.NewGuid();
    private readonly User _requester, _approver;
    private readonly WorkflowTemplate _workflow = new(RequestType.Equipment, "w", [(ApproverPosition.ItManager, ApprovalScope.Global)]);
    private readonly ITriageRepository _triage = Substitute.For<ITriageRepository>();
    private readonly IServiceRequestRepository _requestRepo = Substitute.For<IServiceRequestRepository>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly ServiceRequest _first, _second;

    public PendingTriageTests()
    {
        _requester = User.CreateRequester("r@servicedesk.local", "1001", "hash", "Omar", _dept);
        _approver = User.CreateApprover("it@servicedesk.local", "3001", "hash", "Sara", _dept, ApproverPosition.ItManager);
        _first = ServiceRequest.Submit(_requester, "A", "D", _workflow, T0);
        _second = ServiceRequest.Submit(_requester, "B", "D", _workflow, T0.AddMinutes(1));

        _userRepo.GetByIdAsync(_approver.Id, Arg.Any<CancellationToken>()).Returns(_approver);
        _userRepo.ListByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<User> { _requester });
        _requestRepo.ListPendingCandidatesAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(new List<ServiceRequest> { _first, _second });
        _triage.ListByRequestIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<TriageSuggestion>());
    }

    private ServiceRequestService Service() => new(_requestRepo, _userRepo, Substitute.For<IWorkflowTemplateRepository>(),
        Substitute.For<IUnitOfWork>(), Substitute.For<IOutboxRepository>(), new ApproverResolver(_userRepo), TimeProvider.System, _triage);

    [Fact]
    public async Task Pending_TriageIsNull_BeforeAnyPost()
    {
        var items = await Service().ListPendingForAsync(_approver.Id, default);

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Null(i.Triage));
    }

    [Fact]
    public async Task Pending_ShowsTheStoredSuggestion_OnlyOnItsOwnRequest()
    {
        _triage.ListByRequestIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TriageSuggestion> { new(_second.Id, RequestType.Leave, TriagePriority.Urgent, [ApproverPosition.HrManager], "Sum", 0.5, T0) });

        var items = await Service().ListPendingForAsync(_approver.Id, default);

        Assert.Null(items.Single(i => i.RequestId == _first.Id).Triage);
        var triage = items.Single(i => i.RequestId == _second.Id).Triage!;
        Assert.Equal(TriagePriority.Urgent, triage.Priority);
        Assert.Equal(_second.Id, triage.RequestId);
    }

    [Fact]
    public async Task Pending_LoadsAllSuggestionsInOneQuery()
    {
        await Service().ListPendingForAsync(_approver.Id, default);

        await _triage.Received(1).ListByRequestIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
        await _triage.DidNotReceiveWithAnyArgs().GetByRequestIdAsync(default, default);
    }
}
