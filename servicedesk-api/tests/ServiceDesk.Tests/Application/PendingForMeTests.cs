using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using ServiceDesk.Api.Controllers;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.DTOs;
using ServiceDesk.Application.Services;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;
using ServiceDesk.Domain.Exceptions;

namespace ServiceDesk.Tests.Application;

public class PendingForMeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    // Same departments, users and workflows as DbSeeder (built directly, the seeder is not called).
    private readonly Department _operations = new("Operations");
    private readonly Department _finance = new("Finance");
    private readonly Department _it = new("IT");
    private readonly Department _hr = new("Human Resources");

    private readonly User _omar, _opsHead, _financeHead, _itManager, _hrManager, _financeManager, _gm, _admin, _admin2;
    private readonly List<User> _users;
    private readonly WorkflowTemplate _equipment, _software, _leave, _purchase;
    private readonly WorkflowTemplate _gmWorkflow = new(RequestType.Equipment, "GM sign-off", [(ApproverPosition.GeneralManager, ApprovalScope.Global)]);
    private readonly List<ServiceRequest> _requests = [];
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IServiceRequestRepository _requestRepo = Substitute.For<IServiceRequestRepository>();

    public PendingForMeTests()
    {
        _omar = User.CreateRequester("requester@servicedesk.local", "1001", "hash", "Omar", _operations.Id);
        _opsHead = User.CreateApprover("ops.head@servicedesk.local", "2001", "hash", "Layla", _operations.Id, ApproverPosition.DepartmentHead);
        _financeHead = User.CreateApprover("finance.head@servicedesk.local", "2002", "hash", "Bruce", _finance.Id, ApproverPosition.DepartmentHead);
        _itManager = User.CreateApprover("it.manager@servicedesk.local", "3001", "hash", "Sara", _it.Id, ApproverPosition.ItManager);
        _hrManager = User.CreateApprover("hr.manager@servicedesk.local", "3002", "hash", "Hana", _hr.Id, ApproverPosition.HrManager);
        _financeManager = User.CreateApprover("finance.manager@servicedesk.local", "3003", "hash", "Karim", _finance.Id, ApproverPosition.FinanceManager);
        _gm = User.CreateApprover("gm@servicedesk.local", "4001", "hash", "Yousef", _operations.Id, ApproverPosition.GeneralManager);
        _admin = User.CreateAdmin("admin@servicedesk.local", "9001", "hash", "Admin", _it.Id);
        _admin2 = User.CreateAdmin("admin2@servicedesk.local", "9002", "hash", "Admin Two", _it.Id);
        _users = [_omar, _opsHead, _financeHead, _itManager, _hrManager, _financeManager, _gm, _admin, _admin2];

        _equipment = new WorkflowTemplate(RequestType.Equipment, "Equipment request", [(ApproverPosition.DepartmentHead, ApprovalScope.RequesterDepartment), (ApproverPosition.ItManager, ApprovalScope.Global)]);
        _software = new WorkflowTemplate(RequestType.SoftwareAccess, "Software access request", [(ApproverPosition.ItManager, ApprovalScope.Global)]);
        _leave = new WorkflowTemplate(RequestType.Leave, "Leave request", [(ApproverPosition.HrManager, ApprovalScope.Global)]);
        _purchase = new WorkflowTemplate(RequestType.Purchase, "Purchase request", [(ApproverPosition.DepartmentHead, ApprovalScope.RequesterDepartment), (ApproverPosition.FinanceManager, ApprovalScope.Global)]);

        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(c => Task.FromResult(_users.FirstOrDefault(u => u.Id == c.Arg<Guid>())));
        _userRepo.ListByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                var ids = c.Arg<IEnumerable<Guid>>().ToHashSet();
                IReadOnlyList<User> found = _users.Where(u => ids.Contains(u.Id)).ToList();
                return Task.FromResult(found);
            });
        // The fake returns every request: the trivial superset, so these tests exercise the domain rule, not a copied SQL filter.
        _requestRepo.ListPendingCandidatesAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                IReadOnlyList<ServiceRequest> all = _requests.ToList();
                return Task.FromResult(all);
            });
    }

    private ServiceRequestService Service()
    {
        var approvers = new ApproverResolver(_userRepo);
        return new ServiceRequestService(_requestRepo, _userRepo, Substitute.For<IWorkflowTemplateRepository>(),
            Substitute.For<IUnitOfWork>(), Substitute.For<IOutboxRepository>(), approvers, TimeProvider.System);
    }

    private ServiceRequest Submit(User requester, WorkflowTemplate workflow, int minutes = 0)
    {
        var request = ServiceRequest.Submit(requester, "Title", "Description", workflow, T0.AddMinutes(minutes));
        _requests.Add(request);
        return request;
    }

    private async Task<List<PendingItemDto>> For(User caller) =>
        (await Service().ListPendingForAsync(caller.Id, CancellationToken.None)).ToList();

    // Offers the step to every user through the real Approve; the first accepted user moves it on.
    private bool TryAdvance(ServiceRequest request)
    {
        foreach (var user in _users)
        {
            try { request.Approve(user, null, T0); return true; }
            catch (ApprovalNotAllowedException) { }
        }
        return false;
    }

    [Fact]
    public async Task DepartmentScope_OpsHeadSeesOperationsRequest_FinanceHeadDoesNot()
    {
        var request = Submit(_omar, _equipment);

        Assert.Equal(request.Id, Assert.Single(await For(_opsHead)).RequestId);
        Assert.Empty(await For(_financeHead));
    }

    [Fact]
    public async Task GlobalScope_AfterOpsHeadApproves_ItManagerSeesIt_OpsHeadNoLongerDoes()
    {
        var request = Submit(_omar, _equipment);
        Assert.Empty(await For(_itManager)); // step 1 is still pending

        request.Approve(_opsHead, null, T0);

        Assert.Equal(request.Id, Assert.Single(await For(_itManager)).RequestId);
        Assert.Empty(await For(_opsHead));
    }

    [Fact]
    public async Task Escalation_OpsHeadSubmits_GeneralManagerSeesIt_OpsHeadDoesNot()
    {
        var request = Submit(_opsHead, _equipment);

        var item = Assert.Single(await For(_gm));
        Assert.Equal(request.Id, item.RequestId);
        Assert.Equal(ApproverPosition.GeneralManager, item.CurrentStep.Position);
        Assert.Equal(ApproverPosition.DepartmentHead, item.CurrentStep.EscalatedFrom);
        Assert.Empty(await For(_opsHead));
        Assert.Empty(await For(_financeHead));
    }

    [Fact]
    public async Task OwnRequest_RequesterAndApproverSubmitterNeverSeeTheirOwn()
    {
        Submit(_omar, _equipment);
        var itOwn = Submit(_itManager, _software); // escalated to GM, still the IT manager's own request
        var gmOwn = Submit(_gm, _gmWorkflow);

        Assert.Empty(await For(_omar));
        Assert.Empty(await For(_itManager));
        var gmList = await For(_gm); // the GM sees the IT manager's escalated request, but never their own
        Assert.Equal(itOwn.Id, Assert.Single(gmList).RequestId);
        Assert.DoesNotContain(gmList, i => i.RequestId == gmOwn.Id);
    }

    [Fact]
    public async Task AdminFallback_GmWorkflowSubmittedByGm_EveryAdminSeesIt_GmDoesNot()
    {
        var request = Submit(_gm, _gmWorkflow);

        Assert.Equal(request.Id, Assert.Single(await For(_admin)).RequestId);
        Assert.Equal(request.Id, Assert.Single(await For(_admin2)).RequestId);
        Assert.Empty(await For(_gm));
    }

    [Fact]
    public async Task Admin_OrdinaryStep_AdminDoesNotSeeIt()
    {
        Submit(_omar, _equipment);
        Submit(_omar, _leave);
        Submit(_opsHead, _purchase); // escalated, but the requester's snapshot position is not the step's position

        Assert.Empty(await For(_admin));
    }

    [Theory]
    [InlineData(RequestStatus.Approved)]
    [InlineData(RequestStatus.Rejected)]
    [InlineData(RequestStatus.Cancelled)]
    [InlineData(RequestStatus.Fulfilled)]
    public async Task FinishedRequests_NeverAppear(RequestStatus finished)
    {
        var request = Submit(_omar, _leave);
        switch (finished)
        {
            case RequestStatus.Rejected: request.Reject(_hrManager, "No.", T0); break;
            case RequestStatus.Cancelled: request.Cancel(_omar, T0); break;
            default:
                request.Approve(_hrManager, null, T0);
                if (finished == RequestStatus.Fulfilled) request.Fulfil(_admin, T0);
                break;
        }
        Assert.Equal(finished, request.Status); // guard: the state was really reached

        foreach (var user in _users)
            Assert.Empty(await For(user));
    }

    [Fact]
    public async Task OrderAndShape_OldestFirst_TriageNullAndFieldsMapped()
    {
        var newer = Submit(_omar, _software, minutes: 10);
        var older = Submit(_omar, _software, minutes: 1);
        var middle = Submit(_omar, _software, minutes: 5);

        var items = await For(_itManager);

        Assert.Equal([older.Id, middle.Id, newer.Id], items.Select(i => i.RequestId));
        var item = items[0];
        Assert.Equal(older.Title, item.Title);
        Assert.Equal(RequestType.SoftwareAccess, item.Type);
        Assert.Equal(RequestStatus.Submitted, item.Status);
        Assert.Equal(_omar.Id, item.RequesterId);
        Assert.Equal("Omar", item.RequesterName);
        Assert.Equal(_omar.DepartmentId, item.DepartmentId);
        Assert.Equal(older.CreatedAt, item.CreatedAt);
        Assert.Equal(1, item.CurrentStep.Order);
        Assert.Equal(ApproverPosition.ItManager, item.CurrentStep.Position);
        Assert.Equal(StepDecision.Pending, item.CurrentStep.Decision);
        Assert.Null(item.Triage);
    }

    [Fact]
    public async Task Json_HasExactlyTheContractKeys_AndTriageIsPresentAsNull()
    {
        Submit(_omar, _software);
        var items = await For(_itManager);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(items, options));
        var item = doc.RootElement[0];

        Assert.Equal(
            ["createdAt", "currentStep", "departmentId", "requestId", "requesterId", "requesterName", "status", "title", "triage", "type"],
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("triage").ValueKind);
        Assert.Equal("SoftwareAccess", item.GetProperty("type").GetString());
        Assert.Equal(
            ["comment", "decidedAt", "decidedById", "decision", "escalatedFrom", "order", "position", "scope"],
            item.GetProperty("currentStep").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task UnknownCaller_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Service().ListPendingForAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task RequesterRoleCaller_GetsEmpty_WithoutQueryingRequests()
    {
        Submit(_opsHead, _equipment);

        Assert.Empty(await For(_omar));
        await _requestRepo.DidNotReceive().ListPendingCandidatesAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequesterNames_LoadedWithOneUserQuery()
    {
        Submit(_itManager, _software);
        Submit(_itManager, _software);
        Submit(_hrManager, _leave);

        var items = await For(_gm); // three escalated requests, two distinct requesters

        Assert.Equal(3, items.Count);
        await _userRepo.Received(1).ListByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Endpoint_AllowsOnlyTheHumanRoles()
    {
        var method = typeof(ServiceRequestsController).GetMethod("ListPendingForMe")!;
        var roles = method.GetCustomAttribute<AuthorizeAttribute>()!.Roles;

        Assert.Equal("Requester,Approver,Admin", roles); // TriageService (M1-4a) is deliberately absent
        Assert.Equal("pending-for-me", method.GetCustomAttribute<Microsoft.AspNetCore.Mvc.HttpGetAttribute>()!.Template);
    }

    [Fact]
    public async Task Exhaustive_EveryUserAgainstEveryRequest_ResultMatchesCanDecideCurrentStep()
    {
        var workflows = new[] { _equipment, _software, _leave, _purchase, _gmWorkflow };
        var minutes = 0;

        foreach (var requester in _users)
        foreach (var workflow in workflows)
        for (var skip = 0; skip < workflow.Steps.Count; skip++)
        {
            var request = Submit(requester, workflow, minutes++);
            for (var i = 0; i < skip; i++)
                TryAdvance(request);
        }
        // One finished request of each kind, so the status rule is part of the comparison.
        var rejected = Submit(_omar, _leave, minutes++); rejected.Reject(_hrManager, "No.", T0);
        var cancelled = Submit(_omar, _leave, minutes++); cancelled.Cancel(_omar, T0);
        var approved = Submit(_omar, _leave, minutes++); approved.Approve(_hrManager, null, T0);
        var fulfilled = Submit(_omar, _leave, minutes++); fulfilled.Approve(_hrManager, null, T0); fulfilled.Fulfil(_admin, T0);

        var seen = new Dictionary<string, int>();
        foreach (var user in _users)
        {
            var ids = (await For(user)).Select(i => i.RequestId).ToHashSet();
            foreach (var request in _requests)
            {
                var expected = request.CanDecideCurrentStep(user);
                Assert.True(expected == ids.Contains(request.Id),
                    $"user={user.DisplayName}, requester={request.RequesterId}, status={request.Status}, expected={expected}");
                if (!expected) continue;

                var kind = request.CurrentStep!.EscalatedFrom is not null ? "escalated"
                    : user.Role == UserRole.Admin ? "adminFallback"
                    : request.CurrentStep.Scope == ApprovalScope.RequesterDepartment ? "department" : "global";
                seen[kind] = seen.GetValueOrDefault(kind) + 1;
            }
            Assert.Equal(ids.Count, (await For(user)).Count); // no duplicates
        }

        // Guards: every case is really reached, and finished requests are in the set.
        Assert.All(new[] { "escalated", "adminFallback", "department", "global" }, k => Assert.True(seen.ContainsKey(k), $"never reached: {k}"));
        Assert.True(_requests.Count >= 40);
        Assert.Contains(_requests, r => r.Status is RequestStatus.Rejected);
        Assert.Contains(_requests, r => r.Status is RequestStatus.Cancelled);
        Assert.Contains(_requests, r => r.Status is RequestStatus.Approved);
        Assert.Contains(_requests, r => r.Status is RequestStatus.Fulfilled);
        Assert.Empty(await For(_omar));
    }
}
