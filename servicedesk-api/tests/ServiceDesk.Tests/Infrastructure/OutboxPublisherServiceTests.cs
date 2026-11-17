using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Services;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Infrastructure.Messaging;

namespace ServiceDesk.Tests.Infrastructure;

// M1-2b: the background loop, tested with a scripted store and broker. No RabbitMQ, no database, no real waiting.
public class OutboxPublisherServiceTests
{
    private enum Step { Empty, Ok, Unavailable, Reject, FullOk, FullReject, DbError }

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(50, 30)]
    public void Backoff_DoublesFromOneSecond_CappedAtThirty(int consecutiveFailures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OutboxPublisherService.BackoffFor(consecutiveFailures));
    }

    [Fact]
    public async Task Loop_IdleBatch_WaitsTwoSeconds()
    {
        var run = await RunAsync(stopAfterDelays: 2, Step.Empty, Step.Empty);

        Assert.Equal([Idle, Idle], run.Delays);
    }

    [Fact]
    public async Task Loop_BrokerUnavailable_BacksOffOneTwoFour_ThenResetsAfterAPassThatWorks()
    {
        var run = await RunAsync(5, Step.Unavailable, Step.Unavailable, Step.Unavailable, Step.Ok, Step.Unavailable);

        static TimeSpan s(int n) => TimeSpan.FromSeconds(n);
        Assert.Equal([s(1), s(2), s(4), Idle, s(1)], run.Delays);
    }

    [Fact]
    public async Task Loop_UnexpectedException_LogsError_BacksOff_AndKeepsRunning()
    {
        var run = await RunAsync(3, Step.DbError, Step.DbError, Step.Ok);

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), Idle], run.Delays);
        Assert.Equal(2, run.Log.Count(e => e.Level == LogLevel.Error));
    }

    [Fact]
    public async Task Loop_FullBatchOfSends_RepeatsImmediately_ThenWaitsWhenNothingIsLeft()
    {
        var run = await RunAsync(1, Step.FullOk, Step.FullOk, Step.Empty);

        Assert.Equal([Idle], run.Delays);
        Assert.Equal([3], run.StoreCallsAtDelay); // three passes happened before the first wait
    }

    [Fact]
    public async Task Loop_FullBatchOfRejected_DoesNotSpin_WaitsIdleDelay()
    {
        var run = await RunAsync(2, Step.FullReject, Step.FullReject);

        Assert.Equal([Idle, Idle], run.Delays);
        Assert.Equal([1, 2], run.StoreCallsAtDelay);
    }

    [Fact]
    public async Task Loop_CreatesANewScopePerPass()
    {
        var run = await RunAsync(3, Step.Ok, Step.Empty, Step.Ok);

        Assert.Equal(3, run.ScopesCreated);
    }

    [Fact]
    public async Task Loop_RejectedRows_LoggedAtWarning_WithIdAndType()
    {
        var run = await RunAsync(1, Step.Reject);

        var warning = Assert.Single(run.Log, e => e.Level == LogLevel.Warning);
        Assert.Contains(run.LastRowId.ToString(), warning.Message);
        Assert.Contains("request.submitted", warning.Message);
    }

    [Fact]
    public async Task Loop_Cancellation_StopsCleanly_NoException()
    {
        var script = new Script([]);
        await using var provider = BuildProvider(script);
        var service = new OutboxPublisherService(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<OutboxPublisherService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100); // let the loop reach its real idle wait
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(script.StoreCalls >= 1);
        Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask!.Status);
    }

    // ---- harness ----

    private static async Task<Run> RunAsync(int stopAfterDelays, params Step[] steps)
    {
        var script = new Script(steps);
        var log = new ListLogger<OutboxPublisherService>();
        await using var provider = BuildProvider(script);
        var service = new TestableService(provider.GetRequiredService<IServiceScopeFactory>(), log, script, stopAfterDelays);

        await service.StartAsync(CancellationToken.None);
        await service.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        return new Run(service.Delays, service.StoreCallsAtDelay, script.ScopesCreated, log.Entries, script.LastRowId);
    }

    private static ServiceProvider BuildProvider(Script script)
    {
        var services = new ServiceCollection();
        services.AddScoped<IOutboxDispatchStore>(_ => { script.ScopesCreated++; return new ScriptedStore(script); });
        services.AddScoped<IEventBroker>(_ => new ScriptedBroker(script));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<OutboxDispatcher>();
        return services.BuildServiceProvider();
    }

    private sealed record Run(
        List<TimeSpan> Delays, List<int> StoreCallsAtDelay, int ScopesCreated, List<LogEntry> Log, Guid LastRowId);

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class Script(IReadOnlyList<Step> steps)
    {
        public int StoreCalls;
        public int ScopesCreated;
        public Guid LastRowId;
        public readonly Dictionary<Guid, Step> RowSteps = [];

        public Step NextStep() => StoreCalls <= steps.Count ? steps[StoreCalls - 1] : Step.Empty;
    }

    private sealed class ScriptedStore(Script script) : IOutboxDispatchStore
    {
        public Task<IReadOnlyList<OutboxMessage>> GetUnsentAsync(int limit, CancellationToken ct)
        {
            script.StoreCalls++;
            var step = script.NextStep();
            if (step == Step.DbError) throw new InvalidOperationException("database is down");

            var count = step switch { Step.Empty => 0, Step.FullOk or Step.FullReject => limit, _ => 1 };
            var rows = new List<OutboxMessage>();
            for (var i = 0; i < count; i++)
            {
                var row = new OutboxMessage(Guid.NewGuid(), "request.submitted", Guid.NewGuid(), DateTimeOffset.UtcNow, "{}");
                script.RowSteps[row.Id] = step;
                script.LastRowId = row.Id;
                rows.Add(row);
            }
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(rows);
        }

        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ScriptedBroker(Script script) : IEventBroker
    {
        public Task PublishAsync(OutboxMessage message, CancellationToken ct) => script.RowSteps[message.Id] switch
        {
            Step.Unavailable => throw new BrokerUnavailableException("down"),
            Step.Reject or Step.FullReject => throw new PublishRejectedException("nack"),
            _ => Task.CompletedTask,
        };
    }

    // Records each wait instead of sleeping, and parks after the requested number of waits so the test can stop it.
    private sealed class TestableService(
        IServiceScopeFactory scopes, ILogger<OutboxPublisherService> logger, Script script, int stopAfterDelays)
        : OutboxPublisherService(scopes, logger)
    {
        public readonly List<TimeSpan> Delays = [];
        public readonly List<int> StoreCallsAtDelay = [];
        public readonly TaskCompletionSource Reached = new();

        protected override async Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Delays.Add(delay);
            StoreCallsAtDelay.Add(script.StoreCalls);
            if (Delays.Count < stopAfterDelays) return;
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // parked until StopAsync cancels it
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public readonly List<LogEntry> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
