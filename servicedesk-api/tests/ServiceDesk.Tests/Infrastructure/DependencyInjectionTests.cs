using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ServiceDesk.Infrastructure;

namespace ServiceDesk.Tests.Infrastructure;

public class DependencyInjectionTests
{
    private static IConfiguration BuildConfig(string? connectionString)
    {
        var values = new Dictionary<string, string?>();
        if (connectionString is not null)
        {
            values["ConnectionStrings:Default"] = connectionString;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void AddInfrastructure_MissingConnectionString_ThrowsMessageNamingEnvVar()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddInfrastructure(BuildConfig(null)));

        Assert.Contains("ConnectionStrings__Default", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddInfrastructure_BlankConnectionString_ThrowsMessageNamingEnvVar(string blank)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddInfrastructure(BuildConfig(blank)));

        Assert.Contains("ConnectionStrings__Default", ex.Message);
    }

    [Fact]
    public void AddInfrastructure_ConnectionStringSet_DoesNotThrow()
    {
        var services = new ServiceCollection();

        var result = services.AddInfrastructure(BuildConfig("Server=x;Database=y"));

        Assert.Same(services, result);
    }
}
