using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Infrastructure.Messaging;

namespace ServiceDesk.Tests.Api;

/// <summary>
/// Starts the real API pipeline in memory (like @SpringBootTest + MockMvc). The database, the broker and the
/// background publisher are replaced, so no PostgreSQL or RabbitMQ is needed.
/// </summary>
public sealed class ServiceTokenApiFactory : WebApplicationFactory<Program>
{
    public const string ClientId = "triage-copilot";
    public const string ClientSecret = "test-secret-test-secret-test-secret-1234";
    public const string JwtKey = "dGVzdC1rZXktdGVzdC1rZXktdGVzdC1rZXktdGVzdC1rZXk="; // base64, 32+ bytes

    public IServiceRequestRepository Requests { get; } = Substitute.For<IServiceRequestRepository>();
    public IUserRepository Users { get; } = Substitute.For<IUserRepository>();
    public IWorkflowTemplateRepository Workflows { get; } = Substitute.For<IWorkflowTemplateRepository>();
    public IUnitOfWork UnitOfWork { get; } = Substitute.For<IUnitOfWork>();
    public IOutboxRepository Outbox { get; } = Substitute.For<IOutboxRepository>();
    public ITriageRepository Triage { get; } = Substitute.For<ITriageRepository>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing"); // not Development: no migration, no demo seeding

        // UseSetting, not ConfigureAppConfiguration: Program.cs reads these while it registers services.
        builder.UseSetting("Jwt:Issuer", "ServiceDesk");
        builder.UseSetting("Jwt:Audience", "ServiceDesk.Api");
        builder.UseSetting("Jwt:ExpiryMinutes", "60");
        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("ConnectionStrings:Default", "Host=localhost;Database=unused;Username=u;Password=p");
        builder.UseSetting("RabbitMq:Host", "localhost");
        builder.UseSetting("RabbitMq:User", "u");
        builder.UseSetting("RabbitMq:Password", "p");
        builder.UseSetting("ServiceClients:Triage:ClientId", ClientId);
        builder.UseSetting("ServiceClients:Triage:ClientSecret", ClientSecret);

        builder.ConfigureTestServices(services =>
        {
            foreach (var d in services.Where(d => d.ImplementationType == typeof(OutboxPublisherService)).ToList())
                services.Remove(d);
            Replace(services, Requests);
            Replace(services, Users);
            Replace(services, Workflows);
            Replace(services, UnitOfWork);
            Replace(services, Outbox);
            Replace(services, Triage);
        });
    }

    private static void Replace<T>(IServiceCollection services, T instance) where T : class
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList())
            services.Remove(d);
        services.AddSingleton(instance);
    }
}
