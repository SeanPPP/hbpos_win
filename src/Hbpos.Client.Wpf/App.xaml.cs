using System.IO;
using System.Windows;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hbpos.Client.Wpf;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstanceStartupLease? _startupLease;
    private StartupSplashWindow? _startupSplashWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var startupOptions = AppStartupOptions.FromArgs(e.Args);
        var startupGuard = new SingleInstanceStartupGuard();
        var startupResult = startupGuard.TryAcquire(startupOptions.PreviewMode);
        if (!startupResult.CanStart)
        {
            Shutdown();
            return;
        }

        _startupLease = startupResult.Lease;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!startupOptions.PreviewMode)
        {
            _startupSplashWindow = new StartupSplashWindow();
            _startupSplashWindow.Show();
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
        }

        try
        {
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
                    services.AddSingleton<IDeviceAuthorizationProtector, WindowsDpapiDeviceAuthorizationProtector>();
                    services.AddSingleton<DeviceAuthorizationState>();
                    services.AddTransient<DeviceAuthorizationMessageHandler>();
                    services.AddSingleton<ILocalAppSettingsRepository, LocalAppSettingsRepository>();
                    services.AddSingleton<IScannerBindingService, ScannerBindingService>();
                    services.AddSingleton<ILocalDeviceRepository, LocalDeviceRepository>();
                    services.AddSingleton<ILocalCatalogRepository, LocalCatalogRepository>();
                    services.AddSingleton<ILocalOrderRepository, LocalOrderRepository>();
                    services.AddSingleton<ISyncQueueRepository, SyncQueueRepository>();
                    services.AddHttpClient<ICatalogApiClient, CatalogApiClient>(client =>
                    {
                        client.BaseAddress = GetCatalogApiBaseAddress();
                    })
                    .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
                    services.AddHttpClient<IDeviceApiClient, DeviceApiClient>(client =>
                    {
                        client.BaseAddress = GetCatalogApiBaseAddress();
                        client.Timeout = TimeSpan.FromSeconds(3);
                    });
                    services.AddHttpClient<IConnectivityApiClient, ConnectivityApiClient>(client =>
                    {
                        client.BaseAddress = GetCatalogApiBaseAddress();
                        client.Timeout = TimeSpan.FromSeconds(3);
                    });
                    services.AddSingleton<IDeviceFingerprintService, DeviceFingerprintService>();
                    services.AddSingleton<ILocalCatalogSyncService, LocalCatalogSyncService>();
                    services.AddSingleton<IRemoteLookupRefreshService, RemoteLookupRefreshService>();
                    services.AddSingleton<IDisplayTopologyService, DisplayTopologyService>();
                    services.AddSingleton<ICustomerDisplayWindowService, CustomerDisplayWindowService>();
                    services.AddSingleton<RawScannerInputProcessor>();
                    services.AddSingleton<IRawScannerService, RawScannerService>();
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
            await mainWindow.InitializeForStartupAsync();
            FinishStartupExperience();
            MainWindow = mainWindow;
            mainWindow.Show();
            mainWindow.Activate();
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            mainWindow.ContinueStartupAfterShown();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            _ = ReleaseStartupGateAfterClickGuardDelayAsync();

            base.OnStartup(e);
        }
        catch
        {
            FinishStartupExperience();
            _startupLease?.Dispose();
            _startupLease = null;
            throw;
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        FinishStartupExperience();

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }

        _startupLease?.Dispose();
        _startupLease = null;

        base.OnExit(e);
    }

    private async Task ReleaseStartupGateAfterClickGuardDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _startupLease?.ReleaseStartupGate();
    }

    private void FinishStartupExperience()
    {
        if (_startupSplashWindow is null)
        {
            return;
        }

        _startupSplashWindow.Close();
        _startupSplashWindow = null;
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
