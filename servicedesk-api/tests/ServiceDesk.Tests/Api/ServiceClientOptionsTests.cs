using Microsoft.Extensions.Configuration;
using ServiceDesk.Api.Auth;

namespace ServiceDesk.Tests.Api;

public class ServiceClientOptionsTests
{
    private static IConfiguration Config(string? id, string? secret) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceClients:Triage:ClientId"] = id,
            ["ServiceClients:Triage:ClientSecret"] = secret,
        }).Build();

    private static readonly string Secret = new('s', ServiceClientOptions.MinSecretLength);

    [Fact]
    public void FromConfiguration_Valid_ReturnsValues()
    {
        var options = ServiceClientOptions.FromConfiguration(Config("triage", Secret));

        Assert.Equal("triage", options.ClientId);
        Assert.Equal(Secret, options.ClientSecret);
    }

    [Fact]
    public void FromConfiguration_MissingClientId_ThrowsNamingVariable()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ServiceClientOptions.FromConfiguration(Config(null, Secret)));
        Assert.Contains("ServiceClients__Triage__ClientId", ex.Message);
    }

    [Fact]
    public void FromConfiguration_MissingSecret_ThrowsNamingVariable()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ServiceClientOptions.FromConfiguration(Config("triage", null)));
        Assert.Contains("ServiceClients__Triage__ClientSecret", ex.Message);
    }

    [Fact]
    public void FromConfiguration_ShortSecret_ThrowsNamingVariable()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ServiceClientOptions.FromConfiguration(Config("triage", new string('s', ServiceClientOptions.MinSecretLength - 1))));
        Assert.Contains("ServiceClients__Triage__ClientSecret", ex.Message);
    }

    [Fact]
    public void Matches_RequiresBothIdAndSecret()
    {
        var options = ServiceClientOptions.FromConfiguration(Config("triage", Secret));

        Assert.True(options.Matches("triage", Secret));
        Assert.False(options.Matches("other", Secret));
        Assert.False(options.Matches("triage", Secret + "x"));
        Assert.False(options.Matches("triage", ""));
    }

    [Fact]
    public void ToString_DoesNotContainTheSecret()
    {
        var options = ServiceClientOptions.FromConfiguration(Config("triage", Secret));

        Assert.DoesNotContain(Secret, options.ToString());
        Assert.Contains("triage", options.ToString());
    }
}
