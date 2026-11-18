using Microsoft.EntityFrameworkCore;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;
using ServiceDesk.Domain.Exceptions;
using ServiceDesk.Infrastructure.Persistence;

namespace ServiceDesk.Tests.Domain;

// Audit entries carry a per-request Sequence (1, 2, 3...) set by the domain, so the trail has a stable order
// even when entries share a timestamp.
public class AuditSequenceTests
{
    private static readonly Guid OpsDeptId = Guid.NewGuid();
    private static readonly Guid ItDeptId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly User _requester = User.CreateRequester("user@mail.com", "0001", "hash", "Ali", OpsDeptId);
    private readonly User _opsHead = User.CreateApprover("ops.head@mail.com", "2001", "hash", "Layla", OpsDeptId, ApproverPosition.DepartmentHead);
    private readonly User _opsHead2 = User.CreateApprover("ops.head2@mail.com", "2002", "hash", "Omar", OpsDeptId, ApproverPosition.DepartmentHead);
    private readonly User _itManager = User.CreateApprover("itmanager@mail.com", "0003", "hash", "Eobard", ItDeptId, ApproverPosition.ItManager);
    private readonly User _generalManager = User.CreateApprover("gm@mail.com", "0006", "hash", "Diana", ItDeptId, ApproverPosition.GeneralManager);
    private readonly User _admin = User.CreateAdmin("admin@mail.com", "0007", "hash", "Alfred", ItDeptId);

    private readonly WorkflowTemplate _equipment = new(RequestType.Equipment, "Equipment request",
        [(ApproverPosition.DepartmentHead, ApprovalScope.RequesterDepartment), (ApproverPosition.ItManager, ApprovalScope.Global)]);

    private static int[] Sequences(ServiceRequest r) => r.AuditLogs.Select(l => l.Sequence).ToArray();

    [Fact]
    public void Submit_NoEscalation_SubmittedIsSequence1()
    {
        var request = ServiceRequest.Submit(_requester, "Laptop", "Old one", _equipment, Now);

        var log = Assert.Single(request.AuditLogs);
        Assert.Equal(AuditAction.Submitted, log.Action);
        Assert.Equal(1, log.Sequence);
    }

    [Fact]
    public void Submit_Escalation_SubmittedIs1AndEscalatedIs2_WithTheSameTimestamp()
    {
        var request = ServiceRequest.Submit(_opsHead, "Laptop", "Old one", _equipment, Now);

        Assert.Equal(2, request.AuditLogs.Count);
        var submitted = request.AuditLogs.Single(l => l.Action == AuditAction.Submitted);
        var escalated = request.AuditLogs.Single(l => l.Action == AuditAction.StepEscalated);
        Assert.Equal(submitted.OccurredAt, escalated.OccurredAt); // the tie that Sequence breaks
        Assert.Equal(1, submitted.Sequence);
        Assert.Equal(2, escalated.Sequence);
    }

    [Fact]
    public void Approve_AfterEscalatedSubmit_AppendsSequence3()
    {
        var request = ServiceRequest.Submit(_opsHead, "Laptop", "Old one", _equipment, Now);

        request.Approve(_generalManager, null, Now.AddMinutes(1));

        Assert.Equal(3, request.AuditLogs.Single(l => l.Action == AuditAction.StepApproved).Sequence);
        Assert.Equal([1, 2, 3], Sequences(request));
    }

    [Fact]
    public void FullFlow_SequencesAreConsecutiveAndUnique()
    {
        var request = ServiceRequest.Submit(_opsHead, "Laptop", "Old one", _equipment, Now);
        request.Approve(_generalManager, null, Now.AddMinutes(1));
        request.Approve(_itManager, null, Now.AddMinutes(2));
        request.Fulfil(_admin, Now.AddMinutes(3));

        Assert.Equal([1, 2, 3, 4, 5], Sequences(request));
        Assert.Equal(AuditAction.Fulfilled, request.AuditLogs.Single(l => l.Sequence == 5).Action);
    }

    [Fact]
    public void Reject_AppendsNextSequence()
    {
        var request = ServiceRequest.Submit(_requester, "Laptop", "Old one", _equipment, Now);

        request.Reject(_opsHead2, "Not needed", Now.AddMinutes(1));

        Assert.Equal(2, request.AuditLogs.Single(l => l.Action == AuditAction.StepRejected).Sequence);
    }

    [Fact]
    public void Cancel_AppendsNextSequence()
    {
        var request = ServiceRequest.Submit(_requester, "Laptop", "Old one", _equipment, Now);

        request.Cancel(_requester, Now.AddMinutes(1));

        Assert.Equal(2, request.AuditLogs.Single(l => l.Action == AuditAction.Cancelled).Sequence);
    }

    [Fact]
    public void FailedApprove_DoesNotConsumeASequenceNumber()
    {
        var request = ServiceRequest.Submit(_requester, "Laptop", "Old one", _equipment, Now);

        Assert.Throws<ApprovalNotAllowedException>(() => request.Approve(_itManager, null, Now.AddMinutes(1))); // wrong position for step 1
        request.Approve(_opsHead, null, Now.AddMinutes(2));

        Assert.Equal([1, 2], Sequences(request));
    }

    // Model check only: building the model does not open a connection, so no database is needed.
    [Fact]
    public void AuditLog_HasUniqueIndexOnRequestIdAndSequence()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=model-only").Options;
        using var db = new AppDbContext(options);

        var entity = db.Model.FindEntityType(typeof(AuditLog))!;
        var index = Assert.Single(entity.GetIndexes(), i => i.IsUnique);

        Assert.Equal([nameof(AuditLog.ServiceRequestId), nameof(AuditLog.Sequence)], index.Properties.Select(p => p.Name).ToArray());
    }
}
