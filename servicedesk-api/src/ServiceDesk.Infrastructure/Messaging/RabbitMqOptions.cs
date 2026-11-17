using Microsoft.Extensions.Configuration;

namespace ServiceDesk.Infrastructure.Messaging;

/// <summary>Broker connection settings from the RabbitMq section (env vars RabbitMq__Host, __Port, __User, __Password).</summary>
public sealed record RabbitMqOptions(string Host, int Port, string User, string Password)
{
    public const int DefaultPort = 5672;

    public static RabbitMqOptions FromConfiguration(IConfiguration configuration)
    {
        var host = Required(configuration, "Host");
        var user = Required(configuration, "User");
        var password = Required(configuration, "Password");

        var portText = configuration["RabbitMq:Port"];
        var port = DefaultPort;
        if (!string.IsNullOrWhiteSpace(portText) && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
        {
            throw new InvalidOperationException(
                $"RabbitMq__Port is not a valid port number: '{portText}'. Use a number from 1 to 65535, or leave it unset for {DefaultPort}.");
        }

        return new RabbitMqOptions(host, port, user, password);
    }

    private static string Required(IConfiguration configuration, string name)
    {
        var value = configuration[$"RabbitMq:{name}"];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"RabbitMQ setting is missing. Set the environment variable RabbitMq__{name} (see .env.example at the repo root).");
        }

        return value;
    }
}
