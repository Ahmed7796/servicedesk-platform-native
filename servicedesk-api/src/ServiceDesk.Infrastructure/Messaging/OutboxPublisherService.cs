using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceDesk.Application.Services;

namespace ServiceDesk.Infrastructure.Messaging;

/// <summary>
/// Background loop that publishes unsent outbox rows. Each pass uses a fresh DI scope (fresh DbContext).
/// Broker or database trouble is logged and retried with backoff; the loop never throws out of the host.
/// </summary>
public class OutboxPublisherService(IServiceScopeFactory scopes, ILogger<OutboxPublisherService> logger) : BackgroundService
{
    public const int BatchSize = 50;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    /// <summary>1 s, 2 s, 4 s, 8 s, 16 s, then 30 s.</summary>
    public static TimeSpan BackoffFor(int consecutiveFailures) =>
        TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(Math.Max(consecutiveFailures, 1) - 1, 5)));

    /// <summary>The only place the loop waits. Tests override it to record delays instead of sleeping.</summary>
    protected virtual Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // hand control back so host startup is never blocked by the first pass
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = IdleDelay;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
                var result = await dispatcher.DispatchBatchAsync(BatchSize, stoppingToken);

                foreach (var row in result.Rejected)
                {
                    logger.LogWarning(
                        "Broker rejected outbox message {EventId} ({Type}); it stays unsent and will be retried.", row.Id, row.Type);
                }

                if (result.BrokerUnavailable)
                {
                    failures++;
                    delay = BackoffFor(failures);
                    logger.LogWarning("Broker unavailable; {Published} sent this pass. Retrying in {Delay}.", result.Published, delay);
                }
                else
                {
                    failures = 0;
                    if (result.Published == BatchSize) delay = TimeSpan.Zero; // more rows are probably waiting
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                delay = BackoffFor(failures);
                logger.LogError(ex, "Outbox publisher pass failed. Retrying in {Delay}.", delay);
            }

            try
            {
                if (delay > TimeSpan.Zero) await DelayAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
