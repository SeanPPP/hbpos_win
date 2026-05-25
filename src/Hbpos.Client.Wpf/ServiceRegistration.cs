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
        services.AddSingleton<ISuspendedOrderRepository, SuspendedOrderRepository>();
        services.AddSingleton<ISyncQueueRepository, SyncQueueRepository>();
        services.AddHttpClient<ICatalogApiClient, CatalogApiClient>(client =>
        {
            client.BaseAddress = GetApiBaseAddress();
        })
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IDeviceApiClient, DeviceApiClient>(client =>
        {
            client.BaseAddress = GetApiBaseAddress();
            client.Timeout = TimeSpan.FromSeconds(3);
        })
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IConnectivityApiClient, ConnectivityApiClient>(client =>
        {
            client.BaseAddress = GetApiBaseAddress();
            client.Timeout = TimeSpan.FromSeconds(3);
        });
        services.AddHttpClient<IOrderHistoryApiClient, OrderHistoryApiClient>(client =>
        {
            client.BaseAddress = GetApiBaseAddress();
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddSingleton<IDeviceFingerprintService, DeviceFingerprintService>();
        services.AddSingleton<IUiPriorityCoordinator, UiPriorityCoordinator>();
        services.AddSingleton<ILocalCatalogSyncService, LocalCatalogSyncService>();
        services.AddSingleton<IRemoteLookupRefreshService, RemoteLookupRefreshService>();
        services.AddSingleton<ISpecialProductService, SpecialProductService>();
        services.AddSingleton<IShellCultureService, ShellCultureService>();
        services.AddSingleton<IShellCatalogService, ShellCatalogService>();
        services.AddSingleton<IMainShellStartupService, MainShellStartupService>();
        services.AddSingleton<IShellSyncCenterService, ShellSyncCenterService>();
        services.AddSingleton<ICashPaymentWorkflowService, CashPaymentWorkflowService>();
        services.AddSingleton<ISuspendedOrderService, SuspendedOrderService>();
        services.AddSingleton<IRemoteOrderHistoryService, RemoteOrderHistoryService>();
        services.AddSingleton<IReceiptQueryService, ReceiptQueryService>();
        services.AddSingleton<IDeviceRegistrationWorkflowService, DeviceRegistrationWorkflowService>();
        services.AddSingleton<ISpecialProductsWorkflowService, SpecialProductsWorkflowService>();
        services.AddSingleton<ICustomerDisplayOrchestrator, CustomerDisplayOrchestrator>();
        services.AddSingleton<IUserFeedbackService, WindowsMessageBeepUserFeedbackService>();
        services.AddTransient<IPosTerminalWorkflowService>(sp => new PosTerminalWorkflowService(
            sp.GetRequiredService<LocalSellableItemIndex>(),
            sp.GetRequiredService<PosCartService>(),
            uiPriorityCoordinator: sp.GetRequiredService<IUiPriorityCoordinator>(),
            isCatalogSyncActive: () => sp.GetRequiredService<IShellCatalogService>().IsCatalogSyncActive));
        services.AddTransient<PosTerminalWorkflowFactory>(sp => (remoteLookupRefreshAsync, reloadCatalogAsync) =>
            new PosTerminalWorkflowService(
                sp.GetRequiredService<LocalSellableItemIndex>(),
                sp.GetRequiredService<PosCartService>(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync,
                sp.GetRequiredService<IUiPriorityCoordinator>(),
                () => sp.GetRequiredService<IShellCatalogService>().IsCatalogSyncActive));
        services.AddSingleton<IDisplayTopologyService, DisplayTopologyService>();
        services.AddSingleton<ICustomerDisplayWindowService, CustomerDisplayWindowService>();
        services.AddSingleton<RawScannerInputProcessor>();
        services.AddSingleton<IRawScannerService, RawScannerService>();
        services.AddSingleton<LocalSellableItemIndex>();
        services.AddSingleton<PosCartService>();
        services.AddSingleton<CashCheckoutService>();
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<LocalSellableItemIndex>(),
            sp.GetRequiredService<PosCartService>(),
            sp.GetRequiredService<CashCheckoutService>(),
            sp.GetRequiredService<ILocalSchemaService>(),
            sp.GetRequiredService<IShellCultureService>(),
            sp.GetRequiredService<IShellCatalogService>(),
            sp.GetRequiredService<ILocalCatalogRepository>(),
            sp.GetRequiredService<IRemoteLookupRefreshService>(),
            sp.GetRequiredService<ISpecialProductService>(),
            sp.GetRequiredService<IConnectivityApiClient>(),
            sp.GetRequiredService<IMainShellStartupService>(),
            sp.GetRequiredService<ILocalOrderRepository>(),
            sp.GetRequiredService<IShellSyncCenterService>(),
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<ICustomerDisplayOrchestrator>(),
            sp.GetRequiredService<IRawScannerService>(),
            sp.GetRequiredService<IUserFeedbackService>(),
            sp.GetRequiredService<IReceiptQueryService>(),
            sp.GetRequiredService<ICashPaymentWorkflowService>(),
            sp.GetRequiredService<IDeviceRegistrationWorkflowService>(),
            sp.GetRequiredService<ISpecialProductsWorkflowService>(),
            sp.GetRequiredService<PosTerminalWorkflowFactory>(),
            sp.GetRequiredService<ISuspendedOrderService>(),
            sp.GetRequiredService<IRemoteOrderHistoryService>()));
        services.AddSingleton<MainWindow>();

        return services;
    }

    internal static Uri GetApiBaseAddress()
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
