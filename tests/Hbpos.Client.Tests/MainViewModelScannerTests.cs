using System.Windows;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Tests;

public sealed class MainViewModelScannerTests
{
    [Fact]
    public async Task Reset_scanner_binding_command_resets_scanner_and_updates_status()
    {
        var scanner = new FakeRawScannerService();
        var viewModel = new MainViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            new FakeCatalogRepository(),
            new FakeCatalogSyncService(),
            new FakeRemoteLookupRefreshService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository(),
            new FakeDeviceApiClient(),
            new FakeDeviceFingerprintService(),
            new DeviceAuthorizationState(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            scanner);

        await viewModel.ResetScannerBindingCommand.ExecuteAsync(null);

        Assert.Equal(1, scanner.ResetCount);
        Assert.Equal("扫码枪绑定已清除，请在收银页扫描一次重新学习。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task InitializeAsync_ShowsDeviceRegistrationWithoutWaitingForStores()
    {
        var deviceApi = new FakeDeviceApiClient();
        var viewModel = new MainViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            new FakeCatalogRepository(),
            new FakeCatalogSyncService(),
            new FakeRemoteLookupRefreshService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository(),
            deviceApi,
            new FakeDeviceFingerprintService(),
            new DeviceAuthorizationState(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            new FakeRawScannerService());

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Same(viewModel.DeviceRegistration, viewModel.CurrentScreen);
        Assert.Equal("Loading stores...", viewModel.DeviceRegistration.StatusMessage);
        Assert.Equal(0, deviceApi.GetStoresCallCount);
    }

    [Fact]
    public async Task InitializeAsync_LoadsLocalCatalogBeforeShowingPos()
    {
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "9528502522381")]
        };
        var viewModel = new MainViewModel(
            index,
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            catalog,
            new FakeCatalogSyncService(),
            new FakeRemoteLookupRefreshService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
            new FakeDeviceApiClient(),
            new FakeDeviceFingerprintService(),
            new DeviceAuthorizationState(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            new FakeRawScannerService());

        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);

        Assert.Equal(1, catalog.LoadSellableItemsCallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Equal("SKU-001", Assert.Single(index.FindExactMatches("1042", "9528502522381")).ProductCode);

        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(1, catalog.LoadSellableItemsCallCount);
    }

    [Fact]
    public async Task InitializeAsync_WhenLocalCatalogLoadFails_StillShowsPosWithStatusMessage()
    {
        var catalog = new FakeCatalogRepository
        {
            LoadSellableItemsException = new InvalidOperationException("catalog load failed")
        };
        var viewModel = new MainViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            catalog,
            new FakeCatalogSyncService(),
            new FakeRemoteLookupRefreshService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
            new FakeDeviceApiClient(),
            new FakeDeviceFingerprintService(),
            new DeviceAuthorizationState(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            new FakeRawScannerService());

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal(1, catalog.LoadSellableItemsCallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Contains("catalog load failed", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithSecondDisplay_OpensCustomerDisplayWindow()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService
        {
            OpenResult = new CustomerDisplayWindowResult(true, CustomerDisplayWindowService.OpenedStatusKey)
        };
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(1, customerDisplayWindow.OpenCallCount);
        Assert.True(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("Customer display opened on the second display.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithSingleDisplay_ShowsHelpfulStatus()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService
        {
            OpenResult = new CustomerDisplayWindowResult(false, CustomerDisplayWindowService.NoSecondDisplayStatusKey)
        };
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(1, customerDisplayWindow.OpenCallCount);
        Assert.False(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("No second display detected. Customer display was not opened.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ToggleCustomerDisplayWindow_WithSingleDisplay_ShowsHelpfulStatus()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService
        {
            ToggleResult = new CustomerDisplayWindowResult(false, CustomerDisplayWindowService.NoSecondDisplayStatusKey)
        };
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.ToggleCustomerDisplayWindow(null);

        Assert.Equal(1, customerDisplayWindow.ToggleCallCount);
        Assert.False(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("No second display detected. Customer display was not opened.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task CustomerDisplayWindowClosed_UpdatesOpenState()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService
        {
            OpenResult = new CustomerDisplayWindowResult(true, CustomerDisplayWindowService.OpenedStatusKey)
        };
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.True(viewModel.IsCustomerDisplayOpen);

        customerDisplayWindow.RaiseClosed();

        Assert.False(viewModel.IsCustomerDisplayOpen);
    }

    private static LocalDeviceCache CreateAllowedDevice(string storeCode)
    {
        return new LocalDeviceCache(
            "POS-001",
            storeCode,
            "Main Store",
            "HW-001",
            1,
            true,
            null,
            DateTimeOffset.UtcNow,
            "AUTH-001");
    }

    private static SellableItemDto CreateItem(string storeCode, string productCode, string lookupCode)
    {
        return new SellableItemDto(
            storeCode,
            productCode,
            null,
            "Test Item",
            lookupCode,
            null,
            lookupCode,
            9.9m,
            PriceSourceKind.StoreRetailPrice,
            "Store price",
            1m,
            DateTimeOffset.UtcNow,
            null);
    }

    private static MainViewModel CreateAuthorizedMainViewModel(FakeCustomerDisplayWindowService customerDisplayWindow)
    {
        return new MainViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            new FakeCatalogRepository(),
            new FakeCatalogSyncService(),
            new FakeRemoteLookupRefreshService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
            new FakeDeviceApiClient(),
            new FakeDeviceFingerprintService(),
            new DeviceAuthorizationState(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            customerDisplayWindow,
            new FakeRawScannerService());
    }

    private sealed class FakeRawScannerService : IRawScannerService
    {
        public bool IsActive { get; private set; }

        public int ResetCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void Subscribe(string pageId, Action<RawBarcodeScannedEventArgs> handler)
        {
        }

        public void Unsubscribe(string pageId)
        {
        }

        public void SetActivePage(string? pageId)
        {
        }

        public void Start(IntPtr hwnd)
        {
            IsActive = true;
        }

        public void Stop()
        {
            IsActive = false;
        }

        public Task ResetBindingAsync(CancellationToken cancellationToken = default)
        {
            ResetCount++;
            return Task.CompletedTask;
        }

        public IntPtr ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            return IntPtr.Zero;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeLocalSchemaService : ILocalSchemaService
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSettingsRepository : ILocalAppSettingsRepository
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCatalogRepository : ILocalCatalogRepository
    {
        public IReadOnlyList<SellableItemDto> Items { get; init; } = [];

        public Exception? LoadSellableItemsException { get; init; }

        public int LoadSellableItemsCallCount { get; private set; }

        public Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> DeleteByLookupCodesAsync(string storeCode, IEnumerable<string> lookupCodes, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<SellableItemDto?> FindByLookupCodeAsync(string storeCode, string lookupCode, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SellableItemDto?>(null);
        }

        public Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(
            string storeCode,
            string? afterLookupCodeNormalized,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalSellableItemCompareRow>>([]);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default)
        {
            LoadSellableItemsCallCount++;
            return LoadSellableItemsException is null
                ? Task.FromResult(Items)
                : Task.FromException<IReadOnlyList<SellableItemDto>>(LoadSellableItemsException);
        }
    }

    private sealed class FakeCatalogSyncService : ILocalCatalogSyncService
    {
        public Task<LocalCatalogSyncResult> FullSyncAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<CatalogSyncProgress>? progress = null)
        {
            return Task.FromResult(new LocalCatalogSyncResult(storeCode, 0, 0, 0, 0));
        }
    }

    private sealed class FakeRemoteLookupRefreshService : IRemoteLookupRefreshService
    {
        public Task<RemoteLookupRefreshResult> RefreshLookupAsync(
            string storeCode,
            string lookupCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RemoteLookupRefreshResult(storeCode, lookupCode, false, null, 0));
        }
    }

    private sealed class FakeConnectivityApiClient : IConnectivityApiClient
    {
        public Task<bool> CheckOnlineAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class FakeLocalDeviceRepository : ILocalDeviceRepository
    {
        public LocalDeviceCache? Latest { get; init; }

        public Task<LocalDeviceCache?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Latest);
        }

        public Task SaveAsync(DeviceRegisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveAsync(DeviceVerifyResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDeviceApiClient : IDeviceApiClient
    {
        public int GetStoresCallCount { get; private set; }

        public Task<IReadOnlyList<StoreSelectionItem>> GetStoresAsync(CancellationToken cancellationToken = default)
        {
            GetStoresCallCount++;
            return Task.FromResult<IReadOnlyList<StoreSelectionItem>>([]);
        }

        public Task<DeviceRegisterResponse> RegisterAsync(DeviceRegisterRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeviceRegisterResponse(string.Empty, string.Empty, string.Empty, 0, false, null, null));
        }

        public Task<DeviceVerifyResponse> VerifyAsync(DeviceVerifyRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeviceVerifyResponse(string.Empty, string.Empty, string.Empty, 0, false, null, null));
        }
    }

    private sealed class FakeDeviceFingerprintService : IDeviceFingerprintService
    {
        public string GetHardwareId()
        {
            return "HW-001";
        }
    }

    private sealed class FakeLocalOrderRepository : ILocalOrderRepository
    {
        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalOrder?>(null);
        }
    }

    private sealed class FakeSyncQueueRepository : ISyncQueueRepository
    {
        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SyncQueueOverview(0, 0, 0, null));
        }

        public Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SyncQueueListItem>>([]);
        }
    }

    private sealed class FakeCustomerDisplayWindowService : ICustomerDisplayWindowService
    {
        public CustomerDisplayWindowResult OpenResult { get; init; } = new(false, null);

        public CustomerDisplayWindowResult ToggleResult { get; init; } = new(false, null);

        public bool IsOpen { get; private set; }

        public int OpenCallCount { get; private set; }

        public int ToggleCallCount { get; private set; }

        public event EventHandler? Closed;

        public CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, Window? owner)
        {
            OpenCallCount++;
            IsOpen = OpenResult.IsOpen;
            return OpenResult;
        }

        public CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, Window? owner)
        {
            ToggleCallCount++;
            IsOpen = ToggleResult.IsOpen;
            return ToggleResult;
        }

        public void RaiseClosed()
        {
            IsOpen = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }
}
