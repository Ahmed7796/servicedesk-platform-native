using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Infrastructure.Messaging;

/// <summary>
/// Publishes outbox rows to RabbitMQ (contracts/events/README.md, "Transport"). One connection and one channel
/// with publisher confirms, created on first use and recreated after any failure. Registered as a singleton.
/// </summary>
public sealed class RabbitMqEventBroker(RabbitMqOptions options, ILogger<RabbitMqEventBroker> logger) : IEventBroker, IAsyncDisposable
{
    public const string ExchangeName = "servicedesk.events";
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1); // one publish at a time: a channel is not safe for concurrent publishes
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConfirmTimeout);

            try
            {
                var channel = await EnsureChannelAsync(timeout.Token);
                var properties = new BasicProperties
                {
                    ContentType = "application/json",
                    MessageId = message.Id.ToString(),
                    Type = message.Type,
                    DeliveryMode = DeliveryModes.Persistent,
                };

                // With confirmation tracking on, this completes only when the broker acks, and throws PublishException on a nack.
                await channel.BasicPublishAsync(
                    ExchangeName, message.Type, mandatory: false, properties, Encoding.UTF8.GetBytes(message.Body), timeout.Token);
            }
            catch (PublishException ex)
            {
                // A nack: the channel is fine, only this message was refused.
                throw new PublishRejectedException($"Broker nacked message {message.Id}.", ex);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // shutdown, not a broker problem
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException
                                           or RabbitMQClientException or TimeoutException or ObjectDisposedException)
            {
                // Log the cause here: "wrong password" and "broker not started" both surface as BrokerUnavailableException.
                // Host, port and user only; the password is never logged.
                logger.LogWarning(ex, "RabbitMQ publish failed for {Host}:{Port} as user {User}; will reconnect.",
                    options.Host, options.Port, options.User);
                await DiscardAsync(); // channel state is unknown: reconnect on the next publish
                throw new BrokerUnavailableException($"RabbitMQ at {options.Host}:{options.Port} is unavailable: {ex.Message}", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true } open) return open;
        await DiscardAsync();

        var factory = new ConnectionFactory
        {
            HostName = options.Host,
            Port = options.Port,
            UserName = options.User,
            Password = options.Password,
            VirtualHost = "/",
            AutomaticRecoveryEnabled = false, // the outbox loop is the retry mechanism
        };

        _connection = await factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);
        await _channel.ExchangeDeclareAsync(ExchangeName, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);

        logger.LogInformation("Connected to RabbitMQ at {Host}:{Port}.", options.Host, options.Port);
        return _channel;
    }

    private async Task DiscardAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;

        try { if (channel is not null) await channel.DisposeAsync(); } catch (Exception) { /* already broken */ }
        try { if (connection is not null) await connection.DisposeAsync(); } catch (Exception) { /* already broken */ }
    }

    public async ValueTask DisposeAsync()
    {
        await DiscardAsync();
        _gate.Dispose();
    }
}
