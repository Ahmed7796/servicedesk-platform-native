using System.Net;
using ServiceDesk.Api;

namespace ServiceDesk.Tests.Api;

// M1-2b: the --healthcheck mode, tested with a fake HTTP handler (no sockets).
public class HealthProbeTests
{
    [Fact]
    public async Task Probe_NoFlag_ReturnsNull()
    {
        Assert.Null(await HealthProbe.RunAsync([], new FakeHandler(HttpStatusCode.OK)));
    }

    [Fact]
    public async Task Probe_Healthy200_ReturnsZero_AndCallsLocalHealthOnDefaultPort()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);

        Assert.Equal(0, await HealthProbe.RunAsync(["--healthcheck"], handler));
        Assert.Equal("http://localhost:8080/health", handler.LastUri?.ToString());
    }

    [Fact]
    public async Task Probe_503_ReturnsOne()
    {
        Assert.Equal(1, await HealthProbe.RunAsync(["--healthcheck"], new FakeHandler(HttpStatusCode.ServiceUnavailable)));
    }

    [Fact]
    public async Task Probe_ConnectionRefused_ReturnsOne()
    {
        Assert.Equal(1, await HealthProbe.RunAsync(["--healthcheck"], new FakeHandler(throws: new HttpRequestException("refused"))));
    }

    [Fact]
    public async Task Probe_Redirect_ReturnsOne()
    {
        Assert.Equal(1, await HealthProbe.RunAsync(["--healthcheck"], new FakeHandler(HttpStatusCode.TemporaryRedirect)));
    }

    [Theory]
    [InlineData(null, 8080)]
    [InlineData("", 8080)]
    [InlineData("9000", 9000)]
    [InlineData("9000;9001", 9000)]
    [InlineData("junk", 8080)]
    public void PortFrom_ReadsFirstEntryOfAspNetCoreHttpPorts(string? value, int expected)
    {
        Assert.Equal(expected, HealthProbe.PortFrom(value));
    }

    private sealed class FakeHandler(HttpStatusCode status = HttpStatusCode.OK, Exception? throws = null) : HttpMessageHandler
    {
        public Uri? LastUri;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            if (throws is not null) throw throws;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
