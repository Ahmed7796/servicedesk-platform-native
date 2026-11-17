using NSubstitute;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Services;
using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Tests.Application;

// M1-2a: the dispatch logic, tested with a fake store and a fake broker (no database, no RabbitMQ).
public class OutboxDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeStore _store = new();
    private readonly FakeBroker _broker = new();
    private readonly TimeProvider _clock = Substitute.For<TimeProvider>();
    private readonly OutboxDispatcher _dispatcher;

    public OutboxDispatcherTests()
    {
        _clock.GetUtcNow().Returns(Now);
        _store.Calls = _broker.Calls = [];
        _dispatcher = new OutboxDispatcher(_store, _broker, _clock);
    }

    private OutboxMessage AddRow(string type = "request.submitted")
    {
        var row = new OutboxMessage(Guid.NewGuid(), type, Guid.NewGuid(), Now, "{}");
        _store.Rows.Add(row);
        return row;
    }

    [Fact]
    public async Task Dispatch_PublishesRowsInTheOrderTheStoreReturnsThem()
    {
        var a = AddRow(); var b = AddRow(); var c = AddRow();

        var result = await _dispatcher.DispatchBatchAsync(10, CancellationToken.None);

        Assert.Equal([a.Id, b.Id, c.Id], _broker.PublishedIds);
        Assert.Equal(3, result.Published);
        Assert.False(result.BrokerUnavailable);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public async Task Dispatch_MarksSentOnlyAfterPublishReturns()
    {
        AddRow(); AddRow();

        await _dispatcher.DispatchBatchAsync(10, CancellationToken.None);

        // The broker saw every row still unsent, and each row was saved right after its own publish.
        Assert.All(_broker.SentAtSeenByBroker, sentAt => Assert.Null(sentAt));
        Assert.Equal(["publish", "save", "publish", "save"], _broker.Calls);
        Assert.All(_store.Rows, r => Assert.Equal(Now, r.SentAt));
    }

    [Fact]
    public async Task Dispatch_RequestsAtMostBatchSizeRows()
    {
        for (var i = 0; i < 5; i++) AddRow();

        var result = await _dispatcher.DispatchBatchAsync(3, CancellationToken.None);

        Assert.Equal([3], _store.Limits);
        Assert.Equal(3, result.Published);
        Assert.Equal(2, _store.Rows.Count(r => r.SentAt is null));
    }

    [Fact]
    public async Task Dispatch_BrokerUnavailable_StopsBatch_NothingMarked()
    {
        AddRow(); AddRow();
        _broker.Fail = _ => new BrokerUnavailableException("down");

        var result = await _dispatcher.DispatchBatchAsync(10, CancellationToken.None);

        Assert.True(result.BrokerUnavailable);
        Assert.Equal(0, result.Published);
        Assert.Equal(1, _broker.PublishCalls);
        Assert.DoesNotContain("save", _broker.Calls);
        Assert.All(_store.Rows, r => Assert.Null(r.SentAt));
    }

    [Fact]
    public async Task Dispatch_BrokerUnavailableMidBatch_KeepsEarlierRowsSent_LaterUnsent()
    {
        var a = AddRow(); var b = AddRow(); var c = AddRow();
        _broker.Fail = m => m.Id == b.Id ? new BrokerUnavailableException("down") : null;

        var result = await _dispatcher.DispatchBatchAsync(10, CancellationToken.None);

        Assert.True(result.BrokerUnavailable);
        Assert.Equal(1, result.Published);
        Assert.Equal(Now, a.SentAt);
        Assert.Null(b.SentAt);
        Assert.Null(c.SentAt);
        Assert.Equal(2, _broker.PublishCalls);
    }

    [Fact]
    public async Task Dispatch_RejectedRow_IsSkipped_RowsBehindStillPublished()
    {
        var a = AddRow(); var b = AddRow(); var c = AddRow();
        _broker.Fail = m => m.Id == b.Id ? new PublishRejectedException("nack") : null;

        var result = await _dispatcher.DispatchBatchAsync(10, CancellationToken.None);

        Assert.False(result.BrokerUnavailable);
        Assert.Equal(2, result.Published);
        Assert.Equal([b.Id], result.Rejected.Select(r => r.Id));
        Assert.Equal(Now, a.SentAt);
        Assert.Null(b.SentAt);
        Assert.Equal(Now, c.SentAt);
    }

    [Fact]
    public async Task Dispatch_RejectedRow_StaysUnsent_AndIsRetriedNextBatch()
    {
        var a = AddRow(); var b = AddRow();
        var firstAttempt = true;
        _broker.Fail = m =>
        {
            if (m.Id != a.Id || !firstAttempt) return null;
            firstAttempt = false;
            return new PublishRejectedException("nack");
        };

        await _dispatcher.DispatchBatchAsync(10, CancellationToken.None);
        Assert.Null(a.SentAt);
        Assert.Equal(Now, b.SentAt);

        var second = await _dispatcher.DispatchBatchAsync(10, CancellationToken.None);

        Assert.Equal(1, second.Published);
        Assert.Equal(Now, a.SentAt);
    }

    [Fact]
    public async Task Dispatch_SaveAfterConfirm_IgnoresCancellation_ThenStops()
    {
        var a = AddRow(); var b = AddRow();
        using var cts = new CancellationTokenSource();
        _broker.AfterPublish = () => cts.Cancel(); // shutdown arrives right after the broker confirmed row a

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _dispatcher.DispatchBatchAsync(10, cts.Token));

        Assert.Equal(Now, a.SentAt);                       // the confirmed row was still saved
        Assert.False(_store.SaveTokens.Single().CanBeCanceled); // CancellationToken.None
        Assert.Null(b.SentAt);                             // the next row was not published
        Assert.Equal(1, _broker.PublishCalls);
    }

    [Fact]
    public async Task Dispatch_PublishStillPending_NothingMarkedOrSaved_UntilItCompletes()
    {
        var a = AddRow();
        var confirm = new TaskCompletionSource();
        _broker.Hold = () => confirm.Task; // the broker has not confirmed yet

        var dispatch = _dispatcher.DispatchBatchAsync(10, CancellationToken.None);

        Assert.False(dispatch.IsCompleted);
        Assert.Null(a.SentAt);
        Assert.DoesNotContain("save", _broker.Calls);

        confirm.SetResult();
        await dispatch;

        Assert.Equal(Now, a.SentAt);
        Assert.Equal(["publish", "save"], _broker.Calls);
    }

    [Fact]
    public async Task Dispatch_UnexpectedBrokerException_Propagates_EarlierRowsStaySaved_NothingForThisRow()
    {
        var a = AddRow(); var b = AddRow(); var c = AddRow();
        _broker.Fail = m => m.Id == b.Id ? new InvalidOperationException("boom") : null;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _dispatcher.DispatchBatchAsync(10, CancellationToken.None));

        Assert.Equal(Now, a.SentAt);
        Assert.Null(b.SentAt);
        Assert.Null(c.SentAt);
        Assert.Single(_store.SaveTokens); // only row a was saved
    }

    [Fact]
    public async Task Dispatch_SaveFails_Propagates_AndLaterRowsAreNotPublished()
    {
        AddRow(); var b = AddRow();
        _store.SaveFails = new InvalidOperationException("db down");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _dispatcher.DispatchBatchAsync(10, CancellationToken.None));

        Assert.Equal(1, _broker.PublishCalls);
        Assert.Null(b.SentAt);
    }

    private sealed class FakeStore : IOutboxDispatchStore
    {
        public List<OutboxMessage> Rows { get; } = [];
        public List<int> Limits { get; } = [];
        public List<CancellationToken> SaveTokens { get; } = [];
        public List<string> Calls { get; set; } = [];
        public Exception? SaveFails { get; set; }

        // Like the real query: unsent rows only, in list order, at most `limit`.
        public Task<IReadOnlyList<OutboxMessage>> GetUnsentAsync(int limit, CancellationToken ct)
        {
            Limits.Add(limit);
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(Rows.Where(r => r.SentAt is null).Take(limit).ToList());
        }

        public Task SaveAsync(CancellationToken ct)
        {
            if (SaveFails is not null) throw SaveFails;
            Calls.Add("save");
            SaveTokens.Add(ct);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBroker : IEventBroker
    {
        public List<string> Calls { get; set; } = [];
        public List<Guid> PublishedIds { get; } = [];
        public List<DateTimeOffset?> SentAtSeenByBroker { get; } = [];
        public int PublishCalls { get; private set; }
        public Func<OutboxMessage, Exception?>? Fail { get; set; }
        public Action? AfterPublish { get; set; }
        public Func<Task>? Hold { get; set; } // lets a test keep the publish pending

        public Task PublishAsync(OutboxMessage message, CancellationToken ct)
        {
            PublishCalls++;
            Calls.Add("publish");
            SentAtSeenByBroker.Add(message.SentAt);
            var error = Fail?.Invoke(message);
            if (error is not null) throw error;
            PublishedIds.Add(message.Id);
            AfterPublish?.Invoke();
            return Hold?.Invoke() ?? Task.CompletedTask;
        }
    }
}
