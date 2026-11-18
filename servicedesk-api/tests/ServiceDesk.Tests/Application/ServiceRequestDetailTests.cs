using System.Reflection;
using NSubstitute;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.DTOs;
using ServiceDesk.Application.Services;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;
using ServiceDesk.Domain.Exceptions;

namespace ServiceDesk.Tests.Application;

public class ServiceRequestDetailTests
{
    private static readonly Guid DeptId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly IServiceRequestRepository _requests = Substitute.For<IServiceRequestRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ITriageRepository _triage = Substitute.For<ITriageRepository>();
    private readonly User _opsHead = User.CreateApprover("ops@mail.com", "2001", "hash", "Layla", DeptId, ApproverPosition.DepartmentHead);
    private readonly User _itManager = User.CreateApprover("gm@mail.com", "2002", "hash", "Samir", Guid.NewGuid(), ApproverPosition.GeneralManager);
    private readonly ServiceRequestService _service;

    public ServiceRequestDetailTests()
    {
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(Now);
        _service = new ServiceRequestService(_requests, _users, Substitute.For<IWorkflowTemplateRepository>(), Substitute.For<IUnitOfWork>(), Substitute.For<IOutboxRepository>(), new ApproverResolver(_users), timeProvider, _triage);
    }

    // Submitted(1) + StepEscalated(2) with one timestamp, then StepApproved(3). The stored list is then reversed.
    private ServiceRequest ShuffledRequest()
    {
        var workflow = new WorkflowTemplate(RequestType.Equipment, "Equipment", [(ApproverPosition.DepartmentHead, ApprovalScope.RequesterDepartment), (ApproverPosition.GeneralManager, ApprovalScope.Global)]);
        var request = ServiceRequest.Submit(_opsHead, "Laptop", "Old one", workflow, Now);
        request.Approve(_itManager, null, Now.AddMinutes(1));
        var stored = (List<AuditLog>)typeof(ServiceRequest).GetField("_auditLogs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(request)!;
        stored.Reverse();
        _requests.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        _users.ListByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns([_opsHead, _itManager]);
        return request;
    }

    [Fact]
    public async Task GetDetail_TrailIsOldestFirstBySequence_WhenStoredShuffled_AndNamesCome()
    {
        var request = ShuffledRequest();

        var detail = await _service.GetDetailAsync(request.Id, CancellationToken.None);

        Assert.Equal([AuditAction.Submitted, AuditAction.StepEscalated, AuditAction.StepApproved], detail.AuditTrail.Select(a => a.Action));
        Assert.Equal(detail.AuditTrail[0].OccurredAt, detail.AuditTrail[1].OccurredAt); // the tie that Sequence breaks
        Assert.Equal("Layla", detail.RequesterName);
        Assert.Equal(["Layla", "Layla", "Samir"], detail.AuditTrail.Select(a => a.ActorName));
        Assert.Null(detail.AuditTrail[0].StatusBefore);
        var approved = detail.AuditTrail[2]; // actor and details differ from the requester and from null
        var stored = request.AuditLogs.Single(l => l.Action == AuditAction.StepApproved);
        Assert.Equal(_itManager.Id, approved.ActorId);
        Assert.NotEqual(request.RequesterId, approved.ActorId);
        Assert.False(string.IsNullOrEmpty(stored.Details));
        Assert.Equal(stored.Details, approved.Details);
        Assert.Null(detail.Triage);
        await _users.Received(1).ListByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()); // one query for all names
    }

    [Fact]
    public async Task GetDetail_ActorUserMissing_Throws()
    {
        var request = ShuffledRequest();
        _users.ListByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns([_opsHead]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GetDetailAsync(request.Id, CancellationToken.None));
    }

    [Fact]
    public async Task GetDetail_RequestNotFound_Throws()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _service.GetDetailAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
