using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceDesk.Application;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Services;
using ServiceDesk.Infrastructure;
using ServiceDesk.Infrastructure.Messaging;

namespace ServiceDesk.Tests.Infrastructure;

public class OutboxPublisherRegistrationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void AddOutboxPublisher_MissingRabbitMqHost_ThrowsNamingEnvVar()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddOutboxPublisher(Config(("RabbitMq:User", "u"), ("RabbitMq:Password", "p"))));

        Assert.Contains("RabbitMq__Host", ex.Message);
    }

    [Fact]
    public void AddOutboxPublisher_RegistersHostedServiceAndBroker_WithoutConnecting()
    {
        var services = new ServiceCollection();

        services.AddOutboxPublisher(Config(("RabbitMq:Host", "h"), ("RabbitMq:User", "u"), ("RabbitMq:Password", "p")));

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
        Assert.Contains(services, d => d.ServiceType == typeof(IEventBroker));
    }

    [Fact]
    public async Task AddOutboxPublisher_DispatcherAndStore_ResolveFromAScope()
    {
        var config = Config(
            ("ConnectionStrings:Default", "Host=localhost;Database=x;Username=u;Password=p"),
            ("RabbitMq:Host", "h"), ("RabbitMq:User", "u"), ("RabbitMq:Password", "p"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddInfrastructure(config);
        services.AddOutboxPublisher(config);
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        await using var scope = provider.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<OutboxDispatcher>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IOutboxDispatchStore>());
    }
}
