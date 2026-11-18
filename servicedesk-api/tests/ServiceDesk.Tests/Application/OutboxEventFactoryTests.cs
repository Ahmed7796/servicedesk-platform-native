using System.Text.Json;
using System.Text.RegularExpressions;
using ServiceDesk.Application.Events;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Tests.Application;

public class OutboxEventFactoryTests
{
    private static readonly Guid OpsDeptId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 2, 47, 105, TimeSpan.Zero);

    private readonly User _requester = User.CreateRequester("requester@mail.com", "1001", "hash", "Omar", OpsDeptId);
    private readonly User _opsHead = User.CreateApprover("opshead@mail.com", "2001", "hash", "Layla", OpsDeptId, ApproverPosition.DepartmentHead);
    private readonly User _itManager = User.CreateApprover("it@mail.com", "2002", "hash", "Sam", Guid.NewGuid(), ApproverPosition.ItManager);
    private readonly User _admin = User.CreateAdmin("admin@mail.com", "9001", "hash", "Root", OpsDeptId);

    private readonly WorkflowTemplate _equipment = new(RequestType.Equipment, "Equipment request",
        [(ApproverPosition.DepartmentHead, ApprovalScope.RequesterDepartment), (ApproverPosition.ItManager, ApprovalScope.Global)]);

    private ServiceRequest NewRequest() => ServiceRequest.Submit(_requester, "New laptop", "Current one is old", _equipment, Now);

    // ---- Building each event, as the service will in M1-1b ----

    private OutboxMessage BuildSubmitted(ServiceRequest r, params Guid[] approvers) =>
        OutboxEventFactory.Submitted(r, r.RequesterId, Now, approvers);

    private OutboxMessage BuildStepApproved(string? comment, params Guid[] nextApprovers)
    {
        var r = NewRequest();
        var decided = r.CurrentStep!;
        r.Approve(_opsHead, comment, Now);
        return OutboxEventFactory.StepApproved(r, decided, _opsHead.Id, Now, nextApprovers);
    }

    private OutboxMessage BuildApproved(string? comment)
    {
        var r = NewRequest();
        r.Approve(_opsHead, null, Now);
        var decided = r.CurrentStep!;
        r.Approve(_itManager, comment, Now);
        return OutboxEventFactory.Approved(r, decided, _itManager.Id, Now);
    }

    private OutboxMessage BuildRejected(string reason)
    {
        var r = NewRequest();
        var decided = r.CurrentStep!;
        r.Reject(_opsHead, reason, Now);
        return OutboxEventFactory.Rejected(r, decided, _opsHead.Id, Now);
    }

    private OutboxMessage BuildCancelled()
    {
        var r = NewRequest();
        var before = r.Status;
        r.Cancel(_requester, Now);
        return OutboxEventFactory.Cancelled(r, before, _requester.Id, Now);
    }

    private OutboxMessage BuildFulfilled()
    {
        var r = NewRequest();
        r.Approve(_opsHead, null, Now);
        r.Approve(_itManager, null, Now);
        r.Fulfil(_admin, Now);
        return OutboxEventFactory.Fulfilled(r, _admin.Id, Now);
    }

    public static TheoryData<string> EventTypes => new()
    {
        "request.submitted", "request.step_approved", "request.approved",
        "request.rejected", "request.cancelled", "request.fulfilled",
    };

    private OutboxMessage Build(string type) => type switch
    {
        "request.submitted" => BuildSubmitted(NewRequest(), _opsHead.Id),
        "request.step_approved" => BuildStepApproved("Looks fine.", _itManager.Id),
        "request.approved" => BuildApproved("Go ahead."),
        "request.rejected" => BuildRejected("Not in budget."),
        "request.cancelled" => BuildCancelled(),
        "request.fulfilled" => BuildFulfilled(),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    // ---- Contract files (read-only, in the repo's contracts/ folder) ----

    private static string ContractsEventsDir()
    {
        var searched = new List<string>();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "contracts", "events");
            searched.Add(candidate);
            if (File.Exists(Path.Combine(candidate, "envelope.schema.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new Xunit.Sdk.XunitException("contracts/events folder not found. Searched: " + string.Join(" ; ", searched));
    }

    private static JsonElement LoadExample(string type) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsEventsDir(), "examples", type + ".json"))).RootElement.Clone();

    private static JsonElement SchemaDef(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsEventsDir(), "defs.schema.json"))).RootElement
            .GetProperty("$defs").GetProperty(name).Clone();

    private static HashSet<string> SchemaEnum(string name, string keyword = "enum") =>
        SchemaDef(name).GetProperty(keyword).EnumerateArray().Select(e => e.GetString()!).ToHashSet();

    private static JsonElement Body(OutboxMessage m) => JsonDocument.Parse(m.Body).RootElement.Clone();

    // Same key set at every level and same JSON kinds. null and string may swap (nullable fields: comment, escalatedFrom).
    // Arrays: every element of each side must match the first element of the example (when both have elements).
    private static void AssertSameShape(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind == JsonValueKind.Null || actual.ValueKind == JsonValueKind.Null)
        {
            Assert.True(expected.ValueKind is JsonValueKind.Null or JsonValueKind.String && actual.ValueKind is JsonValueKind.Null or JsonValueKind.String, $"{path}: null only swaps with string");
            return;
        }
        Assert.True(expected.ValueKind == actual.ValueKind, $"{path}: expected {expected.ValueKind} but was {actual.ValueKind}");
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var e = expected.EnumerateObject().Select(p => p.Name).Order().ToList();
            var a = actual.EnumerateObject().Select(p => p.Name).Order().ToList();
            Assert.True(e.SequenceEqual(a), $"{path}: keys differ. expected [{string.Join(",", e)}] actual [{string.Join(",", a)}]");
            foreach (var p in expected.EnumerateObject())
                AssertSameShape(p.Value, actual.GetProperty(p.Name), path + "." + p.Name);
        }
        else if (expected.ValueKind == JsonValueKind.Array && expected.GetArrayLength() > 0)
        {
            var first = expected[0];
            foreach (var item in actual.EnumerateArray())
                AssertSameShape(first, item, path + "[]");
        }
    }

    // ---- Shape tests ----

    [Theory]
    [MemberData(nameof(EventTypes))]
    public void Event_HasSameShapeAsContractExample(string type)
    {
        AssertSameShape(LoadExample(type), Body(Build(type)), type);
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public void Event_EnvelopeMatchesRowColumns(string type)
    {
        var row = Build(type);
        var b = Body(row);

        Assert.Equal(type, row.Type);
        Assert.Equal(type, b.GetProperty("type").GetString());
        Assert.Equal(1, b.GetProperty("version").GetInt32());
        Assert.Equal(row.Id, b.GetProperty("eventId").GetGuid());
        Assert.Equal(row.RequestId, b.GetProperty("requestId").GetGuid());
        Assert.Equal(Now, row.OccurredAt);
        Assert.Null(row.SentAt);
        Assert.NotEqual(Guid.Empty, row.Id);
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public void Event_OccurredAtIsUtcWithZ_AndEqualsNow(string type)
    {
        var text = Body(Build(type)).GetProperty("occurredAt").GetString()!;

        Assert.EndsWith("Z", text);
        Assert.Equal(Now, DateTimeOffset.Parse(text));
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public void Event_EnumsAreSchemaNames_AndIdsAreUuids(string type)
    {
        var b = Body(Build(type));
        var statuses = SchemaEnum("requestStatus");
        // approverPosition is a plain string with a pattern; today's values are its `examples`, and the API emits only those.
        var positions = SchemaEnum("approverPosition", keyword: "examples");
        var positionPattern = new Regex(SchemaDef("approverPosition").GetProperty("pattern").GetString()!);
        var scopes = SchemaEnum("approvalScope");
        var types = SchemaEnum("requestType");
        var uuid = new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");

        Assert.Matches(uuid, b.GetProperty("eventId").GetString()!);
        Assert.Matches(uuid, b.GetProperty("requestId").GetString()!);
        Assert.Matches(uuid, b.GetProperty("actorId").GetString()!);

        var payload = b.GetProperty("payload");
        Assert.Matches(uuid, payload.GetProperty("requesterId").GetString()!);
        Assert.Contains(payload.GetProperty("status").GetString()!, statuses);
        if (payload.TryGetProperty("requestType", out var rt)) Assert.Contains(rt.GetString()!, types);
        if (payload.TryGetProperty("previousStatus", out var ps)) Assert.Contains(ps.GetString()!, statuses);

        foreach (var step in StepsIn(payload))
        {
            Assert.Contains(step.GetProperty("position").GetString()!, positions);
            Assert.Matches(positionPattern, step.GetProperty("position").GetString()!);
            Assert.Contains(step.GetProperty("scope").GetString()!, scopes);
            Assert.True(step.GetProperty("order").GetInt32() >= 1);
            // decidedStep has no escalatedFrom; the other step shapes do.
            if (step.TryGetProperty("escalatedFrom", out var ef) && ef.ValueKind != JsonValueKind.Null) Assert.Contains(ef.GetString()!, positions);
        }
    }

    private static IEnumerable<JsonElement> StepsIn(JsonElement payload)
    {
        if (payload.TryGetProperty("steps", out var steps)) foreach (var s in steps.EnumerateArray()) yield return s;
        foreach (var name in new[] { "currentStep", "nextStep", "decidedStep" })
            if (payload.TryGetProperty(name, out var s)) yield return s;
    }

    [Fact]
    public void Event_StatusIsTheSchemaConstForEachType()
    {
        Assert.Equal("Submitted", Body(Build("request.submitted")).GetProperty("payload").GetProperty("status").GetString());
        Assert.Equal("InReview", Body(Build("request.step_approved")).GetProperty("payload").GetProperty("status").GetString());
        Assert.Equal("Approved", Body(Build("request.approved")).GetProperty("payload").GetProperty("status").GetString());
        Assert.Equal("Rejected", Body(Build("request.rejected")).GetProperty("payload").GetProperty("status").GetString());
        Assert.Equal("Cancelled", Body(Build("request.cancelled")).GetProperty("payload").GetProperty("status").GetString());
        Assert.Equal("Fulfilled", Body(Build("request.fulfilled")).GetProperty("payload").GetProperty("status").GetString());
    }

    [Fact]
    public void ContractsFolder_IsFoundFromTestOutput()
    {
        // Guards the finder itself: it must locate the real folder from the test output directory.
        var dir = ContractsEventsDir();

        Assert.True(File.Exists(Path.Combine(dir, "envelope.schema.json")));
    }

    // ---- Values and edge cases ----

    [Fact]
    public void EventId_IsUniquePerRow()
    {
        var r = NewRequest();

        Assert.NotEqual(BuildSubmitted(r).Id, BuildSubmitted(r).Id);
    }

    [Fact]
    public void Submitted_CarriesStepsAndCurrentStepApprovers()
    {
        var r = NewRequest();

        var payload = Body(BuildSubmitted(r, _opsHead.Id)).GetProperty("payload");

        Assert.Equal(2, payload.GetProperty("steps").GetArrayLength());
        Assert.Equal(OpsDeptId, payload.GetProperty("departmentId").GetGuid());
        Assert.Equal("New laptop", payload.GetProperty("title").GetString());
        Assert.Equal("Equipment", payload.GetProperty("requestType").GetString());
        var current = payload.GetProperty("currentStep");
        Assert.Equal(1, current.GetProperty("order").GetInt32());
        Assert.Equal([_opsHead.Id], current.GetProperty("approverUserIds").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("steps")[0].GetProperty("escalatedFrom").ValueKind);
    }

    [Fact]
    public void ApproverUserIds_EveryItemIsANonNullUuidString()
    {
        var uuid = new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
        var submitted = Body(BuildSubmitted(NewRequest(), _opsHead.Id, _admin.Id)).GetProperty("payload").GetProperty("currentStep");
        var stepApproved = Body(BuildStepApproved("ok", _itManager.Id, _admin.Id)).GetProperty("payload").GetProperty("nextStep");

        foreach (var step in new[] { submitted, stepApproved })
        {
            var ids = step.GetProperty("approverUserIds");
            Assert.Equal(2, ids.GetArrayLength());
            foreach (var id in ids.EnumerateArray())
            {
                Assert.Equal(JsonValueKind.String, id.ValueKind);
                Assert.Matches(uuid, id.GetString()!);
            }
        }
    }

    [Fact]
    public void Submitted_EmptyApproverList_IsAnEmptyArray()
    {
        var payload = Body(BuildSubmitted(NewRequest())).GetProperty("payload");

        var ids = payload.GetProperty("currentStep").GetProperty("approverUserIds");
        Assert.Equal(JsonValueKind.Array, ids.ValueKind);
        Assert.Equal(0, ids.GetArrayLength());
    }

    [Fact]
    public void Submitted_EscalatedStep_ShowsEscalatedFromOnSteps()
    {
        var head = User.CreateApprover("h@mail.com", "3001", "hash", "Head", OpsDeptId, ApproverPosition.DepartmentHead);
        var r = ServiceRequest.Submit(head, "Laptop", "Old", _equipment, Now);

        var payload = Body(OutboxEventFactory.Submitted(r, head.Id, Now, [])).GetProperty("payload");

        var first = payload.GetProperty("steps")[0];
        Assert.Equal("GeneralManager", first.GetProperty("position").GetString());
        Assert.Equal("Global", first.GetProperty("scope").GetString());
        Assert.Equal("DepartmentHead", first.GetProperty("escalatedFrom").GetString());
        Assert.Equal("DepartmentHead", payload.GetProperty("currentStep").GetProperty("escalatedFrom").GetString());
    }

    [Fact]
    public void StepApproved_NextStepIsTheStepAfterTheDecidedOne()
    {
        var payload = Body(BuildStepApproved("ok", _itManager.Id)).GetProperty("payload");

        Assert.Equal(1, payload.GetProperty("decidedStep").GetProperty("order").GetInt32());
        var next = payload.GetProperty("nextStep");
        Assert.Equal(2, next.GetProperty("order").GetInt32());
        Assert.Equal("ItManager", next.GetProperty("position").GetString());
        Assert.Equal([_itManager.Id], next.GetProperty("approverUserIds").EnumerateArray().Select(e => e.GetGuid()));
    }

    [Fact]
    public void StepApproved_NullComment_IsJsonNull()
    {
        var payload = Body(BuildStepApproved(null, _itManager.Id)).GetProperty("payload");

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("comment").ValueKind);
    }

    [Fact]
    public void StepApproved_BlankComment_IsJsonNull_NeverEmptyString()
    {
        var payload = Body(BuildStepApproved("   ", _itManager.Id)).GetProperty("payload");

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("comment").ValueKind);
    }

    [Fact]
    public void StepApproved_CommentIsTrimmedAsStored()
    {
        var payload = Body(BuildStepApproved("  fine  ", _itManager.Id)).GetProperty("payload");

        Assert.Equal("fine", payload.GetProperty("comment").GetString());
    }

    [Fact]
    public void Approved_DecidedStepIsTheLastStep()
    {
        var payload = Body(BuildApproved("done")).GetProperty("payload");

        Assert.Equal(2, payload.GetProperty("decidedStep").GetProperty("order").GetInt32());
        Assert.Equal("done", payload.GetProperty("comment").GetString());
    }

    [Fact]
    public void Rejected_CarriesTrimmedReason()
    {
        var payload = Body(BuildRejected("  Not in budget.  ")).GetProperty("payload");

        Assert.Equal("Not in budget.", payload.GetProperty("reason").GetString());
        Assert.Equal(1, payload.GetProperty("decidedStep").GetProperty("order").GetInt32());
    }

    [Fact]
    public void Cancelled_FromInReview_HasPreviousStatusInReview()
    {
        var r = NewRequest();
        r.Approve(_opsHead, null, Now);
        r.Cancel(_requester, Now);

        var payload = Body(OutboxEventFactory.Cancelled(r, RequestStatus.InReview, _requester.Id, Now)).GetProperty("payload");

        Assert.Equal("InReview", payload.GetProperty("previousStatus").GetString());
        Assert.Equal("Cancelled", payload.GetProperty("status").GetString());
    }

    [Fact]
    public void Cancelled_FromSubmitted_HasPreviousStatusSubmitted()
    {
        var payload = Body(BuildCancelled()).GetProperty("payload");

        Assert.Equal("Submitted", payload.GetProperty("previousStatus").GetString());
    }

    [Fact]
    public void ActorId_IsTheActor_NotTheRequester()
    {
        var b = Body(BuildRejected("no"));

        Assert.Equal(_opsHead.Id, b.GetProperty("actorId").GetGuid());
        Assert.Equal(_requester.Id, b.GetProperty("payload").GetProperty("requesterId").GetGuid());
    }

    [Fact]
    public void OccurredAt_UsesGivenNow_NotTheClock()
    {
        var r = NewRequest();
        var past = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var row = OutboxEventFactory.Submitted(r, r.RequesterId, past, []);

        Assert.Equal(past, row.OccurredAt);
        Assert.StartsWith("2020-01-02T03:04:05", Body(row).GetProperty("occurredAt").GetString());
    }

    [Fact]
    public void OccurredAt_NonUtcOffset_IsWrittenAsUtc()
    {
        var r = NewRequest();
        var local = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.FromHours(3));

        var text = Body(OutboxEventFactory.Submitted(r, r.RequesterId, local, [])).GetProperty("occurredAt").GetString()!;

        Assert.StartsWith("2026-11-16T09:00:00", text);
        Assert.EndsWith("Z", text);
    }

    [Fact]
    public void StepApproved_NoPendingStep_Throws()
    {
        var r = NewRequest();
        var decided = r.CurrentStep!;
        r.Approve(_opsHead, null, Now);
        r.Approve(_itManager, null, Now);

        Assert.Throws<InvalidOperationException>(() => OutboxEventFactory.StepApproved(r, decided, _opsHead.Id, Now, []));
    }
}
