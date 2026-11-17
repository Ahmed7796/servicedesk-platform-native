using NSubstitute;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Services;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;
using ServiceDesk.Domain.Exceptions;

namespace ServiceDesk.Tests.Application;

public class ApproverResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    // Same departments, users and workflows as DbSeeder (built directly, the seeder is not called).
    private readonly Department _operations = new("Operations");
    private readonly Department _finance = new("Finance");
    private readonly Department _it = new("IT");
    private readonly Department _hr = new("Human Resources");

    private readonly List<User> _users;
    private readonly User _opsHead, _financeHead, _itManager, _hrManager, _financeManager, _gm, _admin;
    private readonly User _opsRequester;
    private readonly WorkflowTemplate[] _seedWorkflows;
    private readonly WorkflowTemplate _gmWorkflow = new(RequestType.Equipment, "GM sign-off", [(ApproverPosition.GeneralManager, ApprovalScope.Global)]);

    public ApproverResolverTests()
    {
        _opsRequester = User.CreateRequester("requester@servicedesk.local", "1001", "hash", "Omar", _operations.Id);
        _opsHead = User.CreateApprover("ops.head@servicedesk.local", "2001", "hash", "Layla", _operations.Id, ApproverPosition.DepartmentHead);
        _financeHead = User.CreateApprover("finance.head@servicedesk.local", "2002", "hash", "Bruce", _finance.Id, ApproverPosition.DepartmentHead);
        _itManager = User.CreateApprover("it.manager@servicedesk.local", "3001", "hash", "Sara", _it.Id, ApproverPosition.ItManager);
        _hrManager = User.CreateApprover("hr.manager@servicedesk.local", "3002", "hash", "Hana", _hr.Id, ApproverPosition.HrManager);
        _financeManager = User.CreateApprover("finance.manager@servicedesk.local", "3003", "hash", "Karim", _finance.Id, ApproverPosition.FinanceManager);
        _gm = User.CreateApprover("gm@servicedesk.local", "4001", "hash", "Yousef", _operations.Id, ApproverPosition.GeneralManager);
        _admin = User.CreateAdmin("admin@servicedesk.local", "9001", "hash", "Admin", _it.Id);

        _users = [_opsRequester, _opsHead, _financeHead, _itManager, _hrManager, _financeManager, _gm, _admin];

        _seedWorkflows =
        [
            new WorkflowTemplate(RequestType.Equipment, "Equipment request", [(ApproverPosition.DepartmentHead, ApprovalScope.RequesterDepartment), (ApproverPosition.ItManager, ApprovalScope.Global)]),
            new WorkflowTemplate(RequestType.SoftwareAccess, "Software access request", [(ApproverPosition.ItManager, ApprovalScope.Global)]),
            new WorkflowTemplate(RequestType.Leave, "Leave request", [(ApproverPosition.HrManager, ApprovalScope.Global)]),
            new WorkflowTemplate(RequestType.Purchase, "Purchase request", [(ApproverPosition.DepartmentHead, ApprovalScope.RequesterDepartment), (ApproverPosition.FinanceManager, ApprovalScope.Global)]),
        ];
    }

    // Fake repository: applies the same filter as UserRepository.ListApproverCandidatesAsync.
    private ApproverResolver ResolverOverAllUsers()
    {
        var repo = Substitute.For<IUserRepository>();
        repo.ListApproverCandidatesAsync(Arg.Any<ApproverPosition>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var position = call.Arg<ApproverPosition>();
                IReadOnlyList<User> candidates = _users
                    .Where(u => u.Role == UserRole.Admin || (u.Role == UserRole.Approver && u.Position == position))
                    .ToList();
                return Task.FromResult(candidates);
            });
        return new ApproverResolver(repo);
    }

    // Offers the step to every user through the real Approve; the first accepted user moves it on.
    // Returns false when nobody can decide the step, so the request cannot be advanced further.
    private bool TryAdvance(ServiceRequest request)
    {
        foreach (var user in _users)
        {
            try
            {
                request.Approve(user, null, Now);
                return true;
            }
            catch (ApprovalNotAllowedException)
            {
            }
        }
        return false;
    }

    private ServiceRequest? BuildAtStep(User requester, WorkflowTemplate workflow, int stepsToSkip)
    {
        var request = ServiceRequest.Submit(requester, "Title", "Description", workflow, Now);
        for (var i = 0; i < stepsToSkip; i++)
        {
            if (!TryAdvance(request) || request.CurrentStep is null)
                return null;
        }
        return request.CurrentStep is null ? null : request;
    }

    // Only ApprovalNotAllowedException counts as "refused". Any other exception propagates and fails the test.
    private static bool ApproveAccepts(ServiceRequest request, User user)
    {
        try { request.Approve(user, null, Now); return true; }
        catch (ApprovalNotAllowedException) { return false; }
    }

    private static bool RejectAccepts(ServiceRequest request, User user)
    {
        try { request.Reject(user, "No.", Now); return true; }
        catch (ApprovalNotAllowedException) { return false; }
    }

    [Fact]
    public async Task Resolver_AgreesWithApproveAndReject_ForEveryUserRequesterWorkflowAndStep()
    {
        var resolver = ResolverOverAllUsers();
        var workflows = _seedWorkflows.Append(_gmWorkflow).ToList();
        var scenarios = 0;
        var emptyScenarios = 0;
        var adminFallbackScenarios = 0;

        foreach (var requester in _users)
        foreach (var workflow in workflows)
        for (var step = 0; step < workflow.Steps.Count; step++)
        {
            var reference = BuildAtStep(requester, workflow, step);
            if (reference is null)
                continue; // An earlier step has no eligible approver, so this step is unreachable.

            scenarios++;
            var eligible = await resolver.GetEligibleApproversAsync(reference, CancellationToken.None);
            if (eligible.Count == 0) emptyScenarios++;
            if (reference.RequesterPosition == reference.CurrentStep!.Position) adminFallbackScenarios++;

            foreach (var user in _users)
            {
                var viaApprove = ApproveAccepts(BuildAtStep(requester, workflow, step)!, user);
                var viaReject = RejectAccepts(BuildAtStep(requester, workflow, step)!, user);
                var inList = eligible.Contains(user.Id);
                var context = $"requester={requester.DisplayName}, workflow={workflow.Name}, step={step}, user={user.DisplayName}";

                Assert.True(viaApprove == inList, $"Approve vs list differ: {context}");
                Assert.True(viaReject == inList, $"Reject vs list differ: {context}");
                Assert.True(reference.CanDecideCurrentStep(user) == inList, $"Predicate vs list differ: {context}");
            }
        }

        // Guard against a setup that quietly tests nothing.
        Assert.True(scenarios >= 30, $"Only {scenarios} scenarios were exercised.");
        Assert.True(emptyScenarios > 0, "Expected at least one scenario with no eligible approver.");
        Assert.True(adminFallbackScenarios > 0, "Expected at least one Admin fallback scenario.");
    }

    [Fact]
    public async Task GmRequesterOnGmStep_ListsAdmin_NotTheRequester()
    {
        var request = ServiceRequest.Submit(_gm, "Title", "Description", _gmWorkflow, Now);

        var ids = await ResolverOverAllUsers().GetEligibleApproversAsync(request, CancellationToken.None);

        Assert.Equal([_admin.Id], ids);
    }

    [Fact]
    public async Task GmRequesterOnGmStep_StillIncludesOtherGmHolders()
    {
        var otherGm = User.CreateApprover("gm2@servicedesk.local", "4002", "hash", "Second GM", _finance.Id, ApproverPosition.GeneralManager);
        _users.Add(otherGm);
        var request = ServiceRequest.Submit(_gm, "Title", "Description", _gmWorkflow, Now);

        var ids = await ResolverOverAllUsers().GetEligibleApproversAsync(request, CancellationToken.None);

        Assert.Equal(new[] { _admin.Id, otherGm.Id }.Order(), ids);
    }

    [Fact]
    public async Task AdminRequester_IsExcluded_ButPositionHolderIsListed()
    {
        var request = ServiceRequest.Submit(_admin, "Title", "Description", _seedWorkflows[1], Now); // ItManager step

        var ids = await ResolverOverAllUsers().GetEligibleApproversAsync(request, CancellationToken.None);

        Assert.Equal([_itManager.Id], ids);
    }

    [Fact]
    public async Task DepartmentWithoutHead_GivesEmptyList_AndDoesNotThrow()
    {
        var itRequester = User.CreateRequester("it.req@servicedesk.local", "1002", "hash", "Ivy", _it.Id);
        var request = ServiceRequest.Submit(itRequester, "Title", "Description", _seedWorkflows[0], Now); // DepartmentHead step, IT has none

        var ids = await ResolverOverAllUsers().GetEligibleApproversAsync(request, CancellationToken.None);

        Assert.Empty(ids);
    }

    [Fact]
    public async Task DepartmentScope_ExcludesHeadOfAnotherDepartment()
    {
        var request = ServiceRequest.Submit(_opsRequester, "Title", "Description", _seedWorkflows[0], Now);

        var ids = await ResolverOverAllUsers().GetEligibleApproversAsync(request, CancellationToken.None);

        Assert.Equal([_opsHead.Id], ids);
    }

    [Fact]
    public async Task GlobalScope_IncludesHoldersFromAnyDepartment()
    {
        var second = User.CreateApprover("it2@servicedesk.local", "3101", "hash", "Second IT", _finance.Id, ApproverPosition.ItManager);
        _users.Add(second);
        var request = ServiceRequest.Submit(_opsRequester, "Title", "Description", _seedWorkflows[1], Now);

        var ids = await ResolverOverAllUsers().GetEligibleApproversAsync(request, CancellationToken.None);

        Assert.Equal(new[] { _itManager.Id, second.Id }.Order(), ids);
    }

    [Fact]
    public async Task NonDecidableStatuses_GiveEmptyList_AndDoNotThrow()
    {
        var resolver = ResolverOverAllUsers();

        var approved = ServiceRequest.Submit(_opsRequester, "T", "D", _seedWorkflows[1], Now);
        approved.Approve(_itManager, null, Now);

        var fulfilled = ServiceRequest.Submit(_opsRequester, "T", "D", _seedWorkflows[1], Now);
        fulfilled.Approve(_itManager, null, Now);
        fulfilled.Fulfil(_admin, Now);

        var rejected = ServiceRequest.Submit(_opsRequester, "T", "D", _seedWorkflows[1], Now);
        rejected.Reject(_itManager, "No.", Now);

        var cancelled = ServiceRequest.Submit(_opsRequester, "T", "D", _seedWorkflows[1], Now);
        cancelled.Cancel(_opsRequester, Now);

        foreach (var request in new[] { approved, fulfilled, rejected, cancelled })
            Assert.Empty(await resolver.GetEligibleApproversAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task NoPendingStep_DoesNotQueryTheRepository()
    {
        var repo = Substitute.For<IUserRepository>();
        var request = ServiceRequest.Submit(_opsRequester, "T", "D", _seedWorkflows[1], Now);
        request.Cancel(_opsRequester, Now);

        var ids = await new ApproverResolver(repo).GetEligibleApproversAsync(request, CancellationToken.None);

        Assert.Empty(ids);
        await repo.DidNotReceiveWithAnyArgs().ListApproverCandidatesAsync(default, default);
    }

    [Fact]
    public async Task Resolver_AsksRepositoryForTheCurrentStepsPosition()
    {
        var repo = Substitute.For<IUserRepository>();
        repo.ListApproverCandidatesAsync(Arg.Any<ApproverPosition>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<User>>([]));
        var request = ServiceRequest.Submit(_opsRequester, "T", "D", _seedWorkflows[0], Now);

        await new ApproverResolver(repo).GetEligibleApproversAsync(request, CancellationToken.None);

        await repo.Received(1).ListApproverCandidatesAsync(ApproverPosition.DepartmentHead, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolver_RepositoryReturnsSameUserTwice_ListsTheUserOnce()
    {
        var repo = Substitute.For<IUserRepository>();
        repo.ListApproverCandidatesAsync(Arg.Any<ApproverPosition>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<User>>([_opsHead, _opsHead]));
        var request = ServiceRequest.Submit(_opsRequester, "T", "D", _seedWorkflows[0], Now);

        var ids = await new ApproverResolver(repo).GetEligibleApproversAsync(request, CancellationToken.None);

        Assert.Equal([_opsHead.Id], ids);
    }
}
