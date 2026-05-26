using System.Collections.Concurrent;
using System.Windows;
using System.Globalization;
using System.Reflection;
using System.Windows.Media.Imaging;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Converters;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Tests;

[Collection(ProductThumbnailImageSourceConverterTestCollection.Name)]
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
            new FakeSpecialProductService(),
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
            new FakeSpecialProductService(),
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
            new FakeSpecialProductService(),
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
        Assert.Same(viewModel.PosTerminal, viewModel.CachedPosTerminalScreen);
        Assert.Null(viewModel.CachedSpecialProductsScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.False(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
        Assert.Equal("SKU-001", Assert.Single(index.FindExactMatches("1042", "9528502522381")).ProductCode);

        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(1, catalog.LoadSellableItemsCallCount);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_StartsSpecialProductsPreloadWithoutBlockingPos()
    {
        var catalog = new FakeCatalogRepository
        {
            SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")],
            BeforeLoadSpecialProductItemsAsync = async () => await Task.Delay(25)
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
            new FakeSpecialProductService(),
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

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Equal(0, catalog.LoadSpecialProductItemsCallCount);

        await viewModel.ContinueStartupAfterShownAsync(new AppStartupOptions([], false, null, null));
        await WaitUntilAsync(() => catalog.LoadSpecialProductItemsCallCount > 0);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Same(viewModel.SpecialProducts, viewModel.CachedSpecialProductsScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.False(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_warms_special_product_thumbnails_for_first_page_in_background()
    {
        ClearImageCacheForTests();
        var imageBaseUrl = $"https://images.example/{Guid.NewGuid():N}";
        var catalog = new FakeCatalogRepository
        {
            SpecialItems = Enumerable.Range(1, 21)
                .Select(number => CreateSpecialItem(
                    "1042",
                    $"SKU-{number:000}",
                    $"9528502522{number:000}",
                    imageBaseUrl))
                .ToArray()
        };
        var expectedFirstPageImages = catalog.SpecialItems
            .Take(20)
            .Select(item => item.ProductImage!)
            .ToArray();
        var loadedImages = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var viewModel = new MainViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            catalog,
            new FakeCatalogSyncService(),
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
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
        var converter = new ProductThumbnailImageSourceConverter();
        using var remoteImages = ProductThumbnailImageSourceConverter.UseRemoteImageBytesLoaderForTests((uri, _) =>
        {
            loadedImages.AddOrUpdate(uri.AbsoluteUri, 1, (_, count) => count + 1);
            return Task.FromResult(OnePixelPngBytes());
        });

        var startupOptions = new AppStartupOptions([], false, null, null);
        await viewModel.InitializeAsync(startupOptions);
        Assert.Empty(loadedImages);

        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await WaitUntilAsync(() => expectedFirstPageImages.All(ImageCacheContainsForTests));

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Same(viewModel.SpecialProducts, viewModel.CachedSpecialProductsScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);

        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);
        await viewModel.SpecialProducts!.EnsureLoadedAsync();

        foreach (var item in viewModel.SpecialProducts.PagedSpecialItems)
        {
            Assert.IsType<BitmapImage>(
                converter.Convert(item.ProductImage, typeof(BitmapSource), null, CultureInfo.InvariantCulture));
        }

        Assert.All(expectedFirstPageImages, image => Assert.Equal(1, loadedImages[image]));

        var pageTwoItem = catalog.SpecialItems.Last();
        Assert.IsType<BitmapImage>(
            converter.Convert(pageTwoItem.ProductImage, typeof(BitmapSource), null, CultureInfo.InvariantCulture));
        Assert.Equal(1, loadedImages[pageTwoItem.ProductImage!]);
    }

    [Fact]
    public async Task OpenSpecialProductsCommand_SwitchesScreenWithoutWaitingForLocalLoad()
    {
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeCatalogRepository
        {
            SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")],
            BeforeLoadSpecialProductItemsAsync = () => releaseLoad.Task
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
            new FakeSpecialProductService(),
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

        var openTask = viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);

        Assert.Same(viewModel.SpecialProducts, viewModel.CurrentScreen);
        Assert.Same(viewModel.SpecialProducts, viewModel.CachedSpecialProductsScreen);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        Assert.True(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
        Assert.True(openTask.IsCompleted);
        Assert.Equal(1, catalog.LoadSpecialProductItemsCallCount);

        releaseLoad.SetResult();
        await viewModel.SpecialProducts!.EnsureLoadedAsync();

        Assert.Single(viewModel.SpecialProducts.PagedSpecialItems);
        Assert.Equal(1, catalog.LoadSpecialProductItemsCallCount);
    }

    [Fact]
    public async Task OpenSpecialProductsCommand_reuses_prepared_cached_screen_and_activates_special_host()
    {
        var catalog = new FakeCatalogRepository
        {
            SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
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
            new FakeSpecialProductService(),
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
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await WaitUntilAsync(() => viewModel.CachedSpecialProductsScreen is not null);
        var cachedSpecialProducts = viewModel.CachedSpecialProductsScreen;

        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);

        Assert.Same(cachedSpecialProducts, viewModel.CurrentScreen);
        Assert.Same(cachedSpecialProducts, viewModel.SpecialProducts);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        Assert.True(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
    }

    [Fact]
    public async Task BackFromSpecialProducts_keeps_special_host_cached_and_returns_to_pos()
    {
        var viewModel = new MainViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            new FakeCatalogRepository
            {
                SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
            },
            new FakeCatalogSyncService(),
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
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
        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);
        var cachedSpecialProducts = viewModel.CachedSpecialProductsScreen;

        viewModel.SpecialProducts!.BackCommand.Execute(null);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Same(cachedSpecialProducts, viewModel.CachedSpecialProductsScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.False(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
    }

    [Fact]
    public async Task OpenReturnsCommand_SwitchesToReceiptReturnsScreen()
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
            new FakeSpecialProductService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
            new FakeDeviceApiClient(),
            new FakeDeviceFingerprintService(),
            new DeviceAuthorizationState(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            scanner);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.True(viewModel.PosTerminal!.OpenReturnsCommand.CanExecute(null));

        viewModel.PosTerminal.OpenReturnsCommand.Execute(null);

        Assert.Same(viewModel.ReceiptReturns, viewModel.CurrentScreen);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        Assert.False(viewModel.IsSpecialProductsScreenActive);
        Assert.True(viewModel.IsFallbackScreenActive);
        Assert.Equal(ReceiptReturnsViewModel.PageId, scanner.ActivePageId);
    }

    [Fact]
    public async Task LeavingReceiptReturnsScreen_resets_unconfirmed_return_state()
    {
        var scanner = new FakeRawScannerService();
        var index = new LocalSellableItemIndex();
        var viewModel = new MainViewModel(
            index,
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            new FakeCatalogRepository(),
            new FakeCatalogSyncService(),
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("S001") },
            new FakeDeviceApiClient(),
            new FakeDeviceFingerprintService(),
            new DeviceAuthorizationState(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            scanner);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        index.ReplaceAll([CreateItem("S001", "SKU-RETURN", "690RET")]);
        viewModel.PosTerminal!.OpenReturnsCommand.Execute(null);
        var returns = viewModel.ReceiptReturns!;
        returns.IsNoReceiptMode = true;
        returns.ScanText = "690RET";
        await returns.LookupCommand.ExecuteAsync(null);
        Assert.Single(returns.PendingLines);

        viewModel.ShowPosCommand.Execute(null);

        Assert.Empty(returns.ScanText);
        Assert.False(returns.IsNoReceiptMode);
        Assert.Empty(returns.PendingLines);
        Assert.Empty(returns.OrderLines);
        Assert.False(returns.ReturnRecordsMayBeStale);
        Assert.Equal("No receipt loaded", returns.OrderSummaryText);
    }

    [Fact]
    public async Task KeyboardScannerInput_FromSpecialProductsNormalModeIsConsumedWithoutAddingCart()
    {
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "319844731768")],
            SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
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
            new FakeSpecialProductService(),
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
        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);

        var processed = viewModel.TryProcessKeyboardScannerInput("319844731768");

        Assert.True(processed);
        Assert.Same(viewModel.SpecialProducts, viewModel.CurrentScreen);
        Assert.Empty(viewModel.PosTerminal!.CartLines);
        Assert.Empty(viewModel.SpecialProducts!.SearchResults);
        Assert.Contains("edit", viewModel.SpecialProducts.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeyboardScannerInput_FromSpecialProductsEditModeSearchesCandidatesWithoutAddingCart()
    {
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "319844731768")],
            SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
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
            new FakeSpecialProductService(),
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
        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);
        viewModel.SpecialProducts!.ToggleEditModeCommand.Execute(null);

        var processed = viewModel.TryProcessKeyboardScannerInput("319844731768");

        Assert.True(processed);
        Assert.Same(viewModel.SpecialProducts, viewModel.CurrentScreen);
        Assert.Empty(viewModel.PosTerminal.CartLines);
        Assert.Equal("319844731768", viewModel.SpecialProducts.SearchText);
        var candidate = Assert.Single(viewModel.SpecialProducts.SearchResults);
        Assert.Equal("SKU-001", candidate.ProductCode);
        Assert.Same(candidate, viewModel.SpecialProducts.SelectedSearchResult);
    }

    [Fact]
    public async Task OpenSpecialProductsCommand_ActivatesSpecialProductsScannerPage()
    {
        var scanner = new FakeRawScannerService();
        var viewModel = new MainViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            new FakeCatalogRepository
            {
                SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
            },
            new FakeCatalogSyncService(),
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
            new FakeDeviceApiClient(),
            new FakeDeviceFingerprintService(),
            new DeviceAuthorizationState(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            scanner);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal(PosTerminalViewModel.PageId, scanner.ActivePageId);

        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);

        Assert.Equal(SpecialProductsViewModel.PageId, scanner.ActivePageId);

        viewModel.SpecialProducts!.BackCommand.Execute(null);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Equal(PosTerminalViewModel.PageId, scanner.ActivePageId);
    }

    [Fact]
    public async Task ScannerActivePage_IsClearedForScreensWithoutScannerInputTarget()
    {
        var scanner = new FakeRawScannerService();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "930110")]
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
            new FakeSpecialProductService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
            new FakeDeviceApiClient(),
            new FakeDeviceFingerprintService(),
            new DeviceAuthorizationState(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            scanner);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal(PosTerminalViewModel.PageId, scanner.ActivePageId);

        scanner.Emit("930110");
        viewModel.ShowCashPaymentCommand.Execute(null);

        Assert.Same(viewModel.CashPayment, viewModel.CurrentScreen);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        Assert.False(viewModel.IsSpecialProductsScreenActive);
        Assert.True(viewModel.IsFallbackScreenActive);
        Assert.Null(scanner.ActivePageId);

        await viewModel.ShowPaymentSuccessCommand.ExecuteAsync(null);

        Assert.Same(viewModel.PaymentSuccess, viewModel.CurrentScreen);
        Assert.Null(scanner.ActivePageId);

        await viewModel.ShowHistoryCommand.ExecuteAsync(null);

        Assert.Same(viewModel.TransactionHistory, viewModel.CurrentScreen);
        Assert.Null(scanner.ActivePageId);
    }

    [Fact]
    public async Task ScannerActivePage_IsNullOnDeviceRegistrationScreen()
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
            new FakeSpecialProductService(),
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

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Same(viewModel.DeviceRegistration, viewModel.CurrentScreen);
        Assert.Null(scanner.ActivePageId);
    }

    [Fact]
    public async Task RawScannerInput_OnNonScannerScreenIsIgnoredWithoutChangingCartOrScreen()
    {
        var scanner = new FakeRawScannerService();
        var catalog = new FakeCatalogRepository
        {
            Items =
            [
                CreateItem("1042", "SKU-001", "930110"),
                CreateItem("1042", "SKU-002", "930111")
            ]
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
            new FakeSpecialProductService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
            new FakeDeviceApiClient(),
            new FakeDeviceFingerprintService(),
            new DeviceAuthorizationState(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            scanner);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        scanner.Emit("930110");
        viewModel.ShowCashPaymentCommand.Execute(null);
        var screen = viewModel.CurrentScreen;
        var status = viewModel.StatusMessage;
        var line = Assert.Single(viewModel.PosTerminal!.CartLines);

        scanner.Emit("930111");

        Assert.Same(screen, viewModel.CurrentScreen);
        Assert.Null(scanner.ActivePageId);
        Assert.Equal(status, viewModel.StatusMessage);
        Assert.Same(line, Assert.Single(viewModel.PosTerminal.CartLines));
        Assert.Equal(1m, line.Quantity);
    }

    [Fact]
    public async Task KeyboardScannerInput_OnNonScannerScreenIsConsumedWithoutChangingCartOrScreen()
    {
        var scanner = new FakeRawScannerService();
        var catalog = new FakeCatalogRepository
        {
            Items =
            [
                CreateItem("1042", "SKU-001", "930110"),
                CreateItem("1042", "SKU-002", "930111")
            ]
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
            new FakeSpecialProductService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
            new FakeDeviceApiClient(),
            new FakeDeviceFingerprintService(),
            new DeviceAuthorizationState(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            scanner);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        scanner.Emit("930110");
        viewModel.ShowCashPaymentCommand.Execute(null);
        var screen = viewModel.CurrentScreen;
        var status = viewModel.StatusMessage;
        var line = Assert.Single(viewModel.PosTerminal!.CartLines);

        var processed = viewModel.TryProcessKeyboardScannerInput("930111");

        Assert.True(processed);
        Assert.Same(screen, viewModel.CurrentScreen);
        Assert.Null(scanner.ActivePageId);
        Assert.Equal(status, viewModel.StatusMessage);
        Assert.Same(line, Assert.Single(viewModel.PosTerminal.CartLines));
        Assert.Equal(1m, line.Quantity);
    }

    [Fact]
    public async Task RawScannerInput_FromSpecialProductsEditModeSearchesCandidatesWithoutAddingCart()
    {
        var scanner = new FakeRawScannerService();
        var viewModel = new MainViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            new FakeCatalogRepository
            {
                Items = [CreateItem("1042", "SKU-001", "319844731768")],
                SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
            },
            new FakeCatalogSyncService(),
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
            new FakeDeviceApiClient(),
            new FakeDeviceFingerprintService(),
            new DeviceAuthorizationState(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            scanner);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);
        viewModel.SpecialProducts!.ToggleEditModeCommand.Execute(null);

        scanner.Emit("319844731768");

        Assert.Same(viewModel.SpecialProducts, viewModel.CurrentScreen);
        Assert.Empty(viewModel.PosTerminal.CartLines);
        Assert.Equal("319844731768", viewModel.SpecialProducts.SearchText);
        Assert.Equal("SKU-001", Assert.Single(viewModel.SpecialProducts.SearchResults).ProductCode);
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
            new FakeSpecialProductService(),
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
    public async Task ContinueStartupAfterShownAsync_WithSecondDisplay_OpensCustomerDisplayWindowFullscreen()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(1, customerDisplayWindow.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, viewModel.CustomerDisplayWindowMode);
        Assert.True(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("Customer display opened full screen on the second display.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithSingleDisplay_ShowsHelpfulStatus()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService
        {
            SetModeResult = new CustomerDisplayWindowResult(
                CustomerDisplayWindowMode.Closed,
                CustomerDisplayWindowService.NoSecondDisplayStatusKey)
        };
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(1, customerDisplayWindow.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
        Assert.False(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("No second display detected. Customer display was not opened.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ToggleCustomerDisplayWindow_WithSingleDisplay_ShowsHelpfulStatus()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService
        {
            SetModeResult = new CustomerDisplayWindowResult(
                CustomerDisplayWindowMode.Closed,
                CustomerDisplayWindowService.NoSecondDisplayStatusKey)
        };
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.ToggleCustomerDisplayWindow(null);

        Assert.Equal(1, customerDisplayWindow.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Normal, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
        Assert.False(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("No second display detected. Customer display was not opened.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ToggleCustomerDisplayWindow_CyclesClosedNormalFullscreenClosed()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        viewModel.ToggleCustomerDisplayWindow(null);

        Assert.Equal(CustomerDisplayWindowMode.Normal, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Normal, viewModel.CustomerDisplayWindowMode);
        Assert.True(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("Customer display opened in a normal window on the second display.", viewModel.StatusMessage);

        viewModel.ToggleCustomerDisplayWindow(null);

        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, viewModel.CustomerDisplayWindowMode);
        Assert.True(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("Customer display opened full screen on the second display.", viewModel.StatusMessage);

        viewModel.ToggleCustomerDisplayWindow(null);

        Assert.Equal(CustomerDisplayWindowMode.Closed, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
        Assert.False(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("Customer display closed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task CustomerDisplayWindowClosed_UpdatesOpenState()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.True(viewModel.IsCustomerDisplayOpen);
        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, viewModel.CustomerDisplayWindowMode);

        customerDisplayWindow.RaiseClosed();

        Assert.False(viewModel.IsCustomerDisplayOpen);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
    }

    [Fact]
    public async Task ReregisterDevice_WithPendingSync_StaysOnPosAndShowsStatus()
    {
        var viewModel = new MainViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            new FakeCatalogRepository(),
            new FakeCatalogSyncService(),
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
            new FakeDeviceApiClient(),
            new FakeDeviceFingerprintService(),
            new DeviceAuthorizationState(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository { Overview = new SyncQueueOverview(1, 0, 0, null) },
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            new FakeRawScannerService());

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.PosTerminal!.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Contains("待同步", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReregisterDevice_SubmitSuccess_ClearsAuthorizationAndShowsRegistration()
    {
        var authorizationState = new DeviceAuthorizationState();
        var deviceApi = new FakeDeviceApiClient
        {
            Stores =
            [
                new StoreSelectionItem("1042", "Old Store", true),
                new StoreSelectionItem("2042", "New Store", true)
            ],
            ReregisterResponse = new DeviceReregisterResponse("POS-NEW", "2042", "New Store", -1, false, "Pending approval")
        };
        var viewModel = new MainViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            new FakeCatalogRepository(),
            new FakeCatalogSyncService(),
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new FakeConnectivityApiClient(),
            new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
            deviceApi,
            new FakeDeviceFingerprintService(),
            authorizationState,
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            new FakeRawScannerService());

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        Assert.NotNull(authorizationState.Current);

        await viewModel.PosTerminal!.ReregisterDeviceCommand.ExecuteAsync(null);
        await viewModel.DeviceRegistration!.RegisterCommand.ExecuteAsync(null);

        Assert.Null(authorizationState.Current);
        Assert.Same(viewModel.DeviceRegistration, viewModel.CurrentScreen);
        Assert.Equal("POS-NEW", viewModel.DeviceRegistration.DeviceCode);
        Assert.Equal("2042", deviceApi.LastReregisterRequest?.TargetStoreCode);
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

    private static SellableItemDto CreateSpecialItem(
        string storeCode,
        string productCode,
        string lookupCode,
        string imageBaseUrl)
    {
        return CreateItem(storeCode, productCode, lookupCode) with
        {
            ProductImage = $"{imageBaseUrl}/{productCode}.jpg",
            IsSpecialProduct = true
        };
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
            new FakeSpecialProductService(),
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

    private static byte[] OnePixelPngBytes()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static void ClearImageCacheForTests()
    {
        ClearConcurrentDictionaryField("Cache");
        ClearConcurrentDictionaryField("LoggedDiagnostics");
    }

    private static int GetImageCacheCountForTests()
    {
        var field = typeof(ProductThumbnailImageSourceConverter).GetField("Cache", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var countProperty = field!.FieldType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(countProperty);
        return (int)countProperty!.GetValue(field.GetValue(null))!;
    }

    private static bool ImageCacheContainsForTests(string sourceText)
    {
        var field = typeof(ProductThumbnailImageSourceConverter).GetField("Cache", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var cache = field!.GetValue(null);
        var containsKeyMethod = field.FieldType.GetMethod("ContainsKey", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(containsKeyMethod);
        return (bool)containsKeyMethod!.Invoke(cache, [$"72|{sourceText}"])!;
    }

    private static void ClearConcurrentDictionaryField(string fieldName)
    {
        var field = typeof(ProductThumbnailImageSourceConverter).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var clearMethod = field!.FieldType.GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(clearMethod);
        clearMethod!.Invoke(field.GetValue(null), null);
    }

    private sealed class FakeRawScannerService : IRawScannerService
    {
        private readonly Dictionary<string, Action<RawBarcodeScannedEventArgs>> _handlers = [];

        public bool IsActive { get; private set; }

        public int ResetCount { get; private set; }

        public string? ActivePageId { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void Subscribe(string pageId, Action<RawBarcodeScannedEventArgs> handler)
        {
            _handlers[pageId] = handler;
        }

        public void Unsubscribe(string pageId)
        {
            _handlers.Remove(pageId);
        }

        public void SetActivePage(string? pageId)
        {
            ActivePageId = pageId;
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

        public void Emit(string barcode, DateTimeOffset? scannedAt = null)
        {
            if (ActivePageId is not null && _handlers.TryGetValue(ActivePageId, out var handler))
            {
                handler(new RawBarcodeScannedEventArgs(barcode, "scanner-device", scannedAt ?? DateTimeOffset.Now));
            }
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

        public IReadOnlyList<SellableItemDto> SpecialItems { get; init; } = [];

        public Exception? LoadSellableItemsException { get; init; }

        public int LoadSellableItemsCallCount { get; private set; }

        public int LoadSpecialProductItemsCallCount { get; private set; }

        public Func<Task>? BeforeLoadSpecialProductItemsAsync { get; init; }

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

        public Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            LoadSpecialProductItemsCallCount++;
            if (BeforeLoadSpecialProductItemsAsync is not null)
            {
                return LoadSpecialProductItemsCoreAsync();
            }

            return Task.FromResult<IReadOnlyList<SellableItemDto>>(SpecialItems);

            async Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsCoreAsync()
            {
                await BeforeLoadSpecialProductItemsAsync();
                return SpecialItems;
            }
        }

        public Task SaveSpecialProductOrderAsync(
            string storeCode,
            IEnumerable<string> productCodes,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> UpdateSpecialProductFlagAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> ClearSpecialProductFlagsExceptAsync(
            string storeCode,
            IEnumerable<string> productCodesToKeep,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
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

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            LoadSellableItemsCallCount++;
            return LoadSellableItemsException is null
                ? Task.FromResult<IReadOnlyList<SellableItemDto>>(Items.Where(item => string.Equals(item.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase)).ToArray())
                : Task.FromException<IReadOnlyList<SellableItemDto>>(LoadSellableItemsException);
        }
    }

    private sealed class FakeCatalogSyncService : ILocalCatalogSyncService
    {
        public Task<LocalCatalogSyncResult> FullSyncAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<CatalogSyncProgress>? progress = null,
            bool forceFullDownload = false)
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

    private sealed class FakeSpecialProductService : ISpecialProductService
    {
        public Task<SpecialProductMarkResult> MarkSpecialProductAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SpecialProductMarkResult([], []));
        }

        public Task<SpecialProductDownloadResult> DownloadSpecialProductsAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<SpecialProductDownloadProgress>? progress = null)
        {
            return Task.FromResult(new SpecialProductDownloadResult(storeCode, 0, 0, 0, 0, 0));
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

        public Task SaveAsync(DeviceReregisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDeviceApiClient : IDeviceApiClient
    {
        public int GetStoresCallCount { get; private set; }

        public IReadOnlyList<StoreSelectionItem> Stores { get; init; } = [];

        public DeviceReregisterResponse? ReregisterResponse { get; init; }

        public DeviceReregisterRequest? LastReregisterRequest { get; private set; }

        public Task<IReadOnlyList<StoreSelectionItem>> GetStoresAsync(CancellationToken cancellationToken = default)
        {
            GetStoresCallCount++;
            return Task.FromResult(Stores);
        }

        public Task<DeviceRegisterResponse> RegisterAsync(DeviceRegisterRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeviceRegisterResponse(string.Empty, string.Empty, string.Empty, 0, false, null, null));
        }

        public Task<DeviceVerifyResponse> VerifyAsync(DeviceVerifyRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeviceVerifyResponse(string.Empty, string.Empty, string.Empty, 0, false, null, null));
        }

        public Task<DeviceReregisterResponse> ReregisterAsync(DeviceReregisterRequest request, CancellationToken cancellationToken = default)
        {
            LastReregisterRequest = request;
            return Task.FromResult(ReregisterResponse ?? new DeviceReregisterResponse("POS-NEW", request.TargetStoreCode, "New Store", -1, false, "Pending approval"));
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

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return GetRecentOrdersAsync(take, cancellationToken);
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalOrder?>(null);
        }
    }

    private sealed class FakeSyncQueueRepository : ISyncQueueRepository
    {
        public SyncQueueOverview Overview { get; init; } = new(0, 0, 0, null);

        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Overview.PendingCount);
        }

        public Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Overview);
        }

        public Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SyncQueueListItem>>([]);
        }
    }

    private sealed class FakeCustomerDisplayWindowService : ICustomerDisplayWindowService
    {
        public CustomerDisplayWindowResult SetModeResult { get; init; } = new(
            CustomerDisplayWindowMode.Fullscreen,
            CustomerDisplayWindowService.OpenedFullscreenStatusKey);

        public bool IsOpen => Mode != CustomerDisplayWindowMode.Closed;

        public CustomerDisplayWindowMode Mode { get; private set; } = CustomerDisplayWindowMode.Closed;

        public int OpenCallCount { get; private set; }

        public int ToggleCallCount { get; private set; }

        public int SetModeCallCount { get; private set; }

        public CustomerDisplayWindowMode LastSetMode { get; private set; } = CustomerDisplayWindowMode.Closed;

        public event EventHandler? Closed;

        public CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, Window? owner)
        {
            OpenCallCount++;
            return SetMode(CustomerDisplayWindowMode.Fullscreen, viewModel, owner);
        }

        public CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, Window? owner)
        {
            ToggleCallCount++;
            var targetMode = Mode == CustomerDisplayWindowMode.Closed
                ? CustomerDisplayWindowMode.Fullscreen
                : CustomerDisplayWindowMode.Closed;
            return SetMode(targetMode, viewModel, owner);
        }

        public CustomerDisplayWindowResult SetMode(CustomerDisplayWindowMode mode, CustomerDisplayViewModel viewModel, Window? owner)
        {
            SetModeCallCount++;
            LastSetMode = mode;

            var result = SetModeResult.StatusMessageKey == CustomerDisplayWindowService.NoSecondDisplayStatusKey
                ? SetModeResult
                : CreateSuccessfulResult(mode);
            Mode = result.Mode;
            return result;
        }

        public void RaiseClosed()
        {
            Mode = CustomerDisplayWindowMode.Closed;
            Closed?.Invoke(this, EventArgs.Empty);
        }

        private static CustomerDisplayWindowResult CreateSuccessfulResult(CustomerDisplayWindowMode mode)
        {
            return mode switch
            {
                CustomerDisplayWindowMode.Normal => new CustomerDisplayWindowResult(
                    CustomerDisplayWindowMode.Normal,
                    CustomerDisplayWindowService.OpenedNormalStatusKey),
                CustomerDisplayWindowMode.Fullscreen => new CustomerDisplayWindowResult(
                    CustomerDisplayWindowMode.Fullscreen,
                    CustomerDisplayWindowService.OpenedFullscreenStatusKey),
                _ => new CustomerDisplayWindowResult(
                    CustomerDisplayWindowMode.Closed,
                    CustomerDisplayWindowService.ClosedStatusKey)
            };
        }
    }
}
