using Microsoft.Extensions.Configuration;
using ServiceDesk.Infrastructure.Messaging;

namespace ServiceDesk.Tests.Infrastructure;

public class RabbitMqOptionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] items) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(items.ToDictionary(i => i.Key, i => i.Value))
            .Build();

    private static readonly (string, string?)[] Complete =
        [("RabbitMq:Host", "rabbitmq"), ("RabbitMq:User", "svc"), ("RabbitMq:Password", "pw")];

    [Theory]
    [InlineData("Host", "RabbitMq__Host")]
    [InlineData("User", "RabbitMq__User")]
    [InlineData("Password", "RabbitMq__Password")]
    public void FromConfiguration_MissingOrBlankValue_ThrowsNamingEnvVar(string missing, string envVar)
    {
        var items = Complete.Where(i => i.Item1 != $"RabbitMq:{missing}").ToList();
        items.Add(($"RabbitMq:{missing}", "  "));

        var ex = Assert.Throws<InvalidOperationException>(
            () => RabbitMqOptions.FromConfiguration(Config(items.ToArray())));

        Assert.Contains(envVar, ex.Message);
    }

    [Fact]
    public void FromConfiguration_PortDefaultsTo5672()
    {
        var options = RabbitMqOptions.FromConfiguration(Config(Complete));

        Assert.Equal(5672, options.Port);
        Assert.Equal("rabbitmq", options.Host);
        Assert.Equal("svc", options.User);
        Assert.Equal("pw", options.Password);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("70000")]
    public void FromConfiguration_InvalidPort_ThrowsNamingEnvVar(string port)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RabbitMqOptions.FromConfiguration(Config([.. Complete, ("RabbitMq:Port", port)])));

        Assert.Contains("RabbitMq__Port", ex.Message);
    }

    [Fact]
    public void FromConfiguration_ValidPort_IsUsed()
    {
        var options = RabbitMqOptions.FromConfiguration(Config([.. Complete, ("RabbitMq:Port", "5673")]));

        Assert.Equal(5673, options.Port);
    }
}
