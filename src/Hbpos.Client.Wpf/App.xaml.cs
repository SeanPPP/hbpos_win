using System.IO;
using System.Windows;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hbpos.Client.Wpf;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var startupOptions = AppStartupOptions.FromArgs(e.Args);

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices(services =>
            {
                services.AddSingleton(startupOptions);
                services.AddSingleton<ILocalizationService, LocalizationService>();
                services.AddSingleton<LocalSqliteStore>(_ =>
                {
                    if (!startupOptions.PreviewMode)
                    {
                        return new LocalSqliteStore();
                    }

                    var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-client-preview-{Environment.ProcessId}.db");
                    return new LocalSqliteStore(databasePath);
                });
                services.AddSingleton<ILocalSchemaService, LocalSchemaService>();
                services.AddSingleton<ILocalAppSettingsRepository, LocalAppSettingsRepository>();
                services.AddSingleton<ILocalCatalogRepository, LocalCatalogRepository>();
                services.AddSingleton<ILocalOrderRepository, LocalOrderRepository>();
                services.AddSingleton<ISyncQueueRepository, SyncQueueRepository>();
                services.AddHttpClient<ICatalogApiClient, CatalogApiClient>(client =>
                {
                    client.BaseAddress = GetCatalogApiBaseAddress();
                });
                services.AddSingleton<ILocalCatalogSyncService, LocalCatalogSyncService>();
                services.AddSingleton<IRemoteLookupRefreshService, RemoteLookupRefreshService>();
                services.AddSingleton<ICustomerDisplayWindowService, CustomerDisplayWindowService>();
                services.AddSingleton<LocalSellableItemIndex>();
                services.AddSingleton<PosCartService>();
                services.AddSingleton<CashCheckoutService>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();
        LocalizationResourceProvider.Instance.Configure(_host.Services.GetRequiredService<ILocalizationService>());

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private static Uri GetCatalogApiBaseAddress()
    {
        var configuredBaseUrl = Environment.GetEnvironmentVariable("HBPOS_API_BASE_URL");
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? "http://localhost:5159/"
            : configuredBaseUrl.Trim();

        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        return new Uri(baseUrl, UriKind.Absolute);
    }
}
