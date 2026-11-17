namespace ServiceDesk.Api;

/// <summary>
/// "dotnet ServiceDesk.Api.dll --healthcheck": asks the already-running API's /health endpoint and reports
/// through the exit code, so the container needs no curl. Runs before the web host is built.
/// </summary>
public static class HealthProbe
{
    public const int DefaultPort = 8080;

    /// <returns>null when the flag is absent (start normally); otherwise the process exit code, 0 healthy or 1 not.</returns>
    public static async Task<int?> RunAsync(string[] args, HttpMessageHandler? handler = null)
    {
        if (!args.Contains("--healthcheck")) return null;

        var port = PortFrom(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));
        // A redirect is not "healthy", so do not follow it; only a 2xx counts.
        handler ??= new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(3) };

        try
        {
            using var response = await client.GetAsync($"http://localhost:{port}/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception)
        {
            return 1; // refused, timed out, anything else
        }
    }

    /// <summary>First port of ASPNETCORE_HTTP_PORTS (a list like "8080;8081"), else 8080.</summary>
    public static int PortFrom(string? httpPorts)
    {
        var first = httpPorts?.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return int.TryParse(first, out var port) && port is >= 1 and <= 65535 ? port : DefaultPort;
    }
}
