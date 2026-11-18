using NSubstitute;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.DTOs;
using ServiceDesk.Application.Services;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;
using ServiceDesk.Domain.Exceptions;

namespace ServiceDesk.Tests.Application;

public class TriageSuggestionServiceTests
{
    private static readonly Guid RequestId = Guid.NewGuid();
    private static readonly DateTimeOffset T1 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private readonly IServiceRequestRepository _requests = Substitute.For<IServiceRequestRepository>();
    private readonly ITriageRepository _triage = Substitute.For<ITriageRepository>();
    private readonly TimeProvider _time = Substitute.For<TimeProvider>();
    private readonly TriageSuggestionService _service;

    public TriageSuggestionServiceTests()
    {
        _requests.ExistsAsync(RequestId, Arg.Any<CancellationToken>()).Returns(true);
        _time.GetUtcNow().Returns(T1);
        _service = new TriageSuggestionService(_requests, _triage, _time);
    }

    private static TriageSuggestionInputDto Valid(string summary = "Needs IT approval", double confidence = 0.8) =>
        new(RequestType.Equipment, TriagePriority.High, [ApproverPosition.ItManager, ApproverPosition.GeneralManager], summary, confidence);

    [Fact]
    public async Task Post_Stores_AllFields_AndSuggestedAtFromTimeProvider()
    {
        var result = await _service.PostAsync(RequestId, Valid(), default);

        Assert.Equal(T1, result.SuggestedAt);
        Assert.Equal(RequestId, result.RequestId);
        Assert.Equal([ApproverPosition.ItManager, ApproverPosition.GeneralManager], result.SuggestedRoute);
        await _triage.Received(1).UpsertAsync(Arg.Is<TriageSuggestion>(s =>
            s.RequestId == RequestId && s.Category == RequestType.Equipment && s.Priority == TriagePriority.High
            && s.Summary == "Needs IT approval" && s.Confidence == 0.8 && s.SuggestedAt == T1 && s.SuggestedRoute.Length == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Post_Again_ReplacesAllFieldsAndSuggestedAt()
    {
        await _service.PostAsync(RequestId, Valid(), default);
        var t2 = T1.AddHours(1);
        _time.GetUtcNow().Returns(t2);

        await _service.PostAsync(RequestId, new(RequestType.Leave, TriagePriority.Low, [ApproverPosition.HrManager], "Second", 0.1), default);

        await _triage.Received(1).UpsertAsync(Arg.Is<TriageSuggestion>(s =>
            s.Category == RequestType.Leave && s.Priority == TriagePriority.Low && s.Summary == "Second"
            && s.Confidence == 0.1 && s.SuggestedAt == t2 && s.SuggestedRoute.SequenceEqual(new[] { ApproverPosition.HrManager })), Arg.Any<CancellationToken>());
        await _triage.Received(2).UpsertAsync(Arg.Any<TriageSuggestion>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Post_TrimsSummary()
    {
        var result = await _service.PostAsync(RequestId, Valid(summary: "  padded  "), default);

        Assert.Equal("padded", result.Summary);
    }

    [Fact]
    public async Task Post_SummaryOf500CharactersAfterTrim_IsAccepted()
    {
        var result = await _service.PostAsync(RequestId, Valid(summary: " " + new string('a', 500) + " "), default);

        Assert.Equal(500, result.Summary.Length);
    }

    [Fact]
    public async Task Post_UnknownRequest_ThrowsNotFound_AndStoresNothing()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _service.PostAsync(Guid.NewGuid(), Valid(), default));

        await _triage.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
    }

    [Fact]
    public async Task Post_OnlyChecksExistence_NeverLoadsOrChangesTheRequest()
    {
        await _service.PostAsync(RequestId, Valid(), default);

        await _requests.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
        _requests.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Fact]
    public void Service_HasNoOutboxOrUnitOfWorkOrUserDependency()
    {
        var deps = typeof(TriageSuggestionService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.Equal([typeof(IServiceRequestRepository), typeof(ITriageRepository), typeof(TimeProvider)], deps);
    }

    public static TheoryData<TriageSuggestionInputDto?> InvalidInputs() => new()
    {
        null,
        Valid(confidence: -0.01),
        Valid(confidence: 1.01),
        Valid(confidence: double.NaN),
        Valid() with { Confidence = null },
        Valid() with { Category = null },
        Valid() with { Priority = null },
        Valid() with { Category = (RequestType)99 },
        Valid() with { Priority = (TriagePriority)99 },
        Valid(summary: ""),
        Valid(summary: "   "),
        Valid(summary: new string('a', 501)),
        Valid() with { Summary = null },
        Valid() with { SuggestedRoute = null },
        Valid() with { SuggestedRoute = [] },
        Valid() with { SuggestedRoute = [.. Enumerable.Repeat(ApproverPosition.ItManager, 6)] },
        Valid() with { SuggestedRoute = [(ApproverPosition)99] },
    };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task Post_InvalidInput_ThrowsArgumentException_AndStoresNothing(TriageSuggestionInputDto? input)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.PostAsync(RequestId, input, default));

        await _triage.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
    }

    [Fact]
    public async Task Post_ConfidenceBoundaries0And1_AndFiveRouteItems_AreAccepted()
    {
        await _service.PostAsync(RequestId, Valid(confidence: 0), default);
        await _service.PostAsync(RequestId, Valid(confidence: 1) with { SuggestedRoute = [.. Enumerable.Repeat(ApproverPosition.ItManager, 5)] }, default);

        await _triage.Received(2).UpsertAsync(Arg.Any<TriageSuggestion>(), Arg.Any<CancellationToken>());
    }
}
