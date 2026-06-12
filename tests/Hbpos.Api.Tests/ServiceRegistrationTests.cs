using Hbpos.Api;
using Hbpos.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class ServiceRegistrationTests
{
    [Fact]
    public void AddHbposApiServices_prefers_async_section_and_ignores_empty_legacy_fallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LinklyCloudBackendAsync:PublicNotificationBaseUrl"] = "https://public.example/callback/",
                ["LinklyCloudBackend:PublicNotificationBaseUrl"] = ""
            })
            .Build();
        var services = new ServiceCollection();

        services.AddHbposApiServices(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LinklyCloudBackendAsyncOptions>>().Value;
        Assert.Equal("https://public.example/callback/", options.PublicNotificationBaseUrl);
    }

    [Fact]
    public void AddHbposApiServices_uses_non_empty_legacy_section_as_fallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LinklyCloudBackend:PublicNotificationBaseUrl"] = "https://legacy-public.example/callback/"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddHbposApiServices(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LinklyCloudBackendAsyncOptions>>().Value;
        Assert.Equal("https://legacy-public.example/callback/", options.PublicNotificationBaseUrl);
    }

    [Fact]
    public void AddHbposApiServices_RegistersAdvertisementSchemaInitializer()
    {
        var services = new ServiceCollection();

        services.AddHbposApiServices();

        var descriptor = Assert.Single(services, x => x.ServiceType == typeof(IAdvertisementSchemaInitializer));
        Assert.Equal(typeof(SqlSugarAdvertisementSchemaInitializer), descriptor.ImplementationType);
    }
}
