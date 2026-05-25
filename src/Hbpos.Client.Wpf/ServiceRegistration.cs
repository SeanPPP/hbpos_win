using System.IO;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Client.Wpf;

public static class ServiceRegistration
{
    public static IServiceCollection AddHbposClientServices(
        this IServiceCollection services,
        AppStartupOptions startupOptions)
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
        })
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IConnectivityApiClient, ConnectivityApiClient>(client =>
        {
            client.BaseAddress = GetCatalogApiBaseAddress();
            client.Timeout = TimeSpan.FromSeconds(3);
        });
        services.AddSingleton<IDeviceFingerprintService, DeviceFingerprintService>();
        services.AddSingleton<ILocalCatalogSyncService, LocalCatalogSyncService>();
        services.AddSingleton<IRemoteLookupRefreshService, RemoteLookupRefreshService>();
        services.AddSingleton<ISpecialProductService, SpecialProductService>();
        services.AddSingleton<IShellCultureService, ShellCultureService>();
        services.AddSingleton<IShellCatalogService, ShellCatalogService>();
        services.AddSingleton<IMainShellStartupService, MainShellStartupService>();
        services.AddSingleton<IShellSyncCenterService, ShellSyncCenterService>();
        services.AddSingleton<ICashPaymentWorkflowService, CashPaymentWorkflowService>();
        services.AddSingleton<IReceiptQueryService, ReceiptQueryService>();
        services.AddSingleton<IDeviceRegistrationWorkflowService, DeviceRegistrationWorkflowService>();
        services.AddSingleton<ISpecialProductsWorkflowService, SpecialProductsWorkflowService>();
        services.AddSingleton<ICustomerDisplayOrchestrator, CustomerDisplayOrchestrator>();
        services.AddTransient<IPosTerminalWorkflowService, PosTerminalWorkflowService>();
        services.AddTransient<PosTerminalWorkflowFactory>(sp => (remoteLookupRefreshAsync, reloadCatalogAsync) =>
            new PosTerminalWorkflowService(
                sp.GetRequiredService<LocalSellableItemIndex>(),
                sp.GetRequiredService<PosCartService>(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync));
        services.AddSingleton<IDisplayTopologyService, DisplayTopologyService>();
        services.AddSingleton<ICustomerDisplayWindowService, CustomerDisplayWindowService>();
        services.AddSingleton<RawScannerInputProcessor>();
        services.AddSingleton<IRawScannerService, RawScannerService>();
        services.AddSingleton<LocalSellableItemIndex>();
        services.AddSingleton<PosCartService>();
        services.AddSingleton<CashCheckoutService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
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
