using System.Security.Cryptography;
using System.Text;

namespace ServiceDesk.Api.Auth;

/// <summary>The triage copilot's client credentials (env vars ServiceClients__Triage__ClientId and __ClientSecret).</summary>
public sealed record ServiceClientOptions(string ClientId, string ClientSecret)
{
    public const int MinSecretLength = 32;

    // A record's generated ToString() prints every property; keep the secret out of logs and exception messages.
    public override string ToString() => $"ServiceClientOptions {{ ClientId = {ClientId}, ClientSecret = *** }}";

    public static ServiceClientOptions FromConfiguration(IConfiguration configuration)
    {
        var id = configuration["ServiceClients:Triage:ClientId"];
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException(
                "Service client setting is missing. Set the environment variable ServiceClients__Triage__ClientId (see .env.example at the repo root).");

        var secret = configuration["ServiceClients:Triage:ClientSecret"];
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException(
                "Service client setting is missing. Set the environment variable ServiceClients__Triage__ClientSecret (see .env.example at the repo root).");
        if (secret.Length < MinSecretLength)
            throw new InvalidOperationException(
                $"ServiceClients__Triage__ClientSecret is too short. Use at least {MinSecretLength} characters (for example: openssl rand -base64 32).");

        return new ServiceClientOptions(id, secret);
    }

    /// <summary>
    /// Constant-time check of both values. Each side is hashed first so the buffers have equal length
    /// (FixedTimeEquals returns early on a length mismatch), and both comparisons always run.
    /// </summary>
    public bool Matches(string clientId, string clientSecret)
    {
        var idOk = FixedTimeEquals(clientId, ClientId);
        var secretOk = FixedTimeEquals(clientSecret, ClientSecret);
        return idOk & secretOk; // single &, not &&: no short-circuit
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a)),
            SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
