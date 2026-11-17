using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Services;

namespace ServiceDesk.Infrastructure.Messaging;

public static class OutboxPublisherRegistration
{
    /// <summary>
    /// Registers the RabbitMQ adapter and the background loop. Called from Program.cs, not from AddInfrastructure,
    /// so code that only needs the repositories does not need RabbitMq settings. Throws at startup if they are missing.
    /// </summary>
    public static IServiceCollection AddOutboxPublisher(this IServiceCollection services, IConfiguration configuration)
    {
        var options = RabbitMqOptions.FromConfiguration(configuration);

        services.AddSingleton<IEventBroker>(sp =>
            new RabbitMqEventBroker(options, sp.GetRequiredService<ILogger<RabbitMqEventBroker>>()));
        services.AddScoped<IOutboxDispatchStore, OutboxDispatchStore>();
        services.AddScoped<OutboxDispatcher>();
        services.AddHostedService<OutboxPublisherService>();

        return services;
    }
}
