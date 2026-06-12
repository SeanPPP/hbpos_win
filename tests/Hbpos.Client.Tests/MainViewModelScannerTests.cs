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
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

[Collection(ProductThumbnailImageSourceConverterTestCollection.Name)]
public sealed class MainViewModelScannerTests
{
    [Fact]
    public async Task Active_page_title_tracks_navigation_and_culture()
    {
        var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService());

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal("POS", viewModel.ActivePageTitleText);

        viewModel.ShowReturnsCommand.Execute(null);
        Assert.Equal("Returns", viewModel.ActivePageTitleText);

        await viewModel.ShowHistoryCommand.ExecuteAsync(null);
        Assert.Equal("History", viewModel.ActivePageTitleText);

        await viewModel.ToggleCultureCommand.ExecuteAsync(null);

        Assert.Equal("\u5386\u53F2", viewModel.ActivePageTitleText);
    }

    [Fact]
    public async Task Active_page_title_uses_payment_mode_for_payment_screen()
    {
        var cart = new PosCartService();
        var viewModel = new MainViewModel(
            new LocalSellableItemIndex(),
            cart,
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
            new FakeRawScannerService());

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        cart.AddReturnLine(new ReturnCartLineRequest(
            "1042",
            "SKU-REFUND-TITLE",
            null,
            "Refund Title Tea",
            "930TITLE1",
            "ITEM-REFUND-TITLE",
            null,
            1m,
            9.9m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-TITLE-1",
            Guid.NewGuid(),
            Guid.NewGuid()));

        viewModel.ShowCashPaymentCommand.Execute(null);
        Assert.Equal("Refund", viewModel.ActivePageTitleText);

        cart.Clear();
        cart.AddItem(CreateItem("1042", "SKU-ZERO-TITLE", "930TITLE2"));
        cart.AddReturnLine(new ReturnCartLineRequest(
            "1042",
            "SKU-ZERO-RET",
            null,
            "Zero Title Return",
            "930TITLE3",
            "ITEM-ZERO-TITLE",
            null,
            1m,
            9.9m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-TITLE-2",
            Guid.NewGuid(),
            Guid.NewGuid()));

        viewModel.ShowCashPaymentCommand.Execute(null);
        Assert.Equal("Zero Settlement", viewModel.ActivePageTitleText);

        await viewModel.ToggleCultureCommand.ExecuteAsync(null);

        cart.Clear();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "1042",
            "SKU-REFUND-TITLE-CN",
            null,
            "Refund Title Tea CN",
            "930TITLE4",
            "ITEM-REFUND-TITLE-CN",
            null,
            1m,
            9.9m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-TITLE-3",
            Guid.NewGuid(),
            Guid.NewGuid()));

        viewModel.ShowCashPaymentCommand.Execute(null);
        Assert.Equal("\u9000\u6B3E", viewModel.ActivePageTitleText);

        cart.Clear();
        cart.AddItem(CreateItem("1042", "SKU-ZERO-TITLE-CN", "930TITLE5"));
        cart.AddReturnLine(new ReturnCartLineRequest(
            "1042",
            "SKU-ZERO-RET-CN",
            null,
            "Zero Title Return CN",
            "930TITLE6",
            "ITEM-ZERO-TITLE-CN",
            null,
            1m,
            9.9m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-TITLE-4",
            Guid.NewGuid(),
            Guid.NewGuid()));

        viewModel.ShowCashPaymentCommand.Execute(null);
        Assert.Equal("\u96F6\u7ED3\u7B97", viewModel.ActivePageTitleText);
    }

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
        Assert.Equal("Scanner binding reset. Trigger the scanner again to bind the current device.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Card_payment_completion_auto_prints_receipt_after_success_screen()
    {
        var printService = new RecordingReceiptPrintService();
        var cashDrawerService = new RecordingCashDrawerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            cashDrawerService: cashDrawerService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card);

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => ReferenceEquals(viewModel.PaymentSuccess, viewModel.CurrentScreen) && printService.Calls.Count == 1);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(order.OrderGuid, call.OrderGuid);
        Assert.Equal(ReceiptPrintReason.CardAuto, call.Reason);
        Assert.Equal(0, cashDrawerService.OpenCallCount);
    }

    [Fact]
    public async Task Card_refund_completion_auto_prints_receipt_after_success_screen()
    {
        var printService = new RecordingReceiptPrintService();
        var cashDrawerService = new RecordingCashDrawerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            cashDrawerService: cashDrawerService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card) with
        {
            TotalAmount = -10m,
            ActualAmount = -10m,
            Payments =
            [
                new LocalPayment(
                    Guid.NewGuid(),
                    PaymentMethodKind.Card,
                    -10m,
                    "CARD-REFUND-123")
            ]
        };

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => ReferenceEquals(viewModel.PaymentSuccess, viewModel.CurrentScreen) && printService.Calls.Count == 1);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(order.OrderGuid, call.OrderGuid);
        Assert.Equal(ReceiptPrintReason.CardAuto, call.Reason);
        Assert.Equal(0, cashDrawerService.OpenCallCount);
    }

    [Fact]
    public async Task Cash_payment_completion_does_not_auto_print_receipt()
    {
        var printService = new RecordingReceiptPrintService();
        var cashDrawerService = new RecordingCashDrawerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            cashDrawerService: cashDrawerService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash);

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => ReferenceEquals(viewModel.PaymentSuccess, viewModel.CurrentScreen) && cashDrawerService.OpenCallCount == 1);
        await Task.Delay(50);
        Assert.Empty(printService.Calls);
        Assert.Equal(1, cashDrawerService.OpenCallCount);
    }

    [Fact]
    public async Task Mixed_cash_card_payment_completion_opens_cash_drawer_and_auto_prints_receipt()
    {
        var printService = new RecordingReceiptPrintService();
        var cashDrawerService = new RecordingCashDrawerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            cashDrawerService: cashDrawerService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash, PaymentMethodKind.Card);

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() =>
            ReferenceEquals(viewModel.PaymentSuccess, viewModel.CurrentScreen) &&
            cashDrawerService.OpenCallCount == 1 &&
            printService.Calls.Count == 1);

        Assert.Equal(1, cashDrawerService.OpenCallCount);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(order.OrderGuid, call.OrderGuid);
        Assert.Equal(ReceiptPrintReason.CardAuto, call.Reason);
    }

    [Fact]
    public async Task Full_card_tender_auto_completes_payment_and_opens_success_screen()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-AUTO-CARD", "930AUTO"));
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var viewModel = CreateAuthorizedMainViewModelWithPaymentWorkflow(
            cart,
            checkout,
            orderRepository,
            syncQueue,
            new ApprovedCardTerminalClient("CARD-AUTO"));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.ShowCashPaymentCommand.Execute(null);

        await viewModel.CashPayment!.SelectCardCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => ReferenceEquals(viewModel.PaymentSuccess, viewModel.CurrentScreen));
        Assert.Empty(cart.Lines);
        Assert.Empty(viewModel.CashPayment.PaymentTenders);
    }

    [Fact]
    public async Task Partial_card_tender_stays_on_payment_screen_and_blocks_back_to_pos()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-PARTIAL-CARD", "930PART"));
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var viewModel = CreateAuthorizedMainViewModelWithPaymentWorkflow(
            cart,
            checkout,
            orderRepository,
            syncQueue,
            new ApprovedCardTerminalClient("CARD-PART"));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.ShowCashPaymentCommand.Execute(null);
        var payment = viewModel.CashPayment!;
        payment.TenderAmountText = "5";

        await payment.SelectCardCommand.ExecuteAsync(null);
        payment.BackToPosCommand.Execute(null);

        Assert.Same(payment, viewModel.CurrentScreen);
        Assert.Single(payment.PaymentTenders);
        Assert.Equal("Remove added tenders or complete the payment before returning to POS.", payment.StatusMessage);
    }

    [Fact]
    public async Task Cash_refund_completion_opens_cash_drawer()
    {
        var cashDrawerService = new RecordingCashDrawerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashDrawerService: cashDrawerService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash) with
        {
            TotalAmount = -10m,
            ActualAmount = -10m,
            Payments = [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, -10m, null)]
        };

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => ReferenceEquals(viewModel.PaymentSuccess, viewModel.CurrentScreen) && cashDrawerService.OpenCallCount == 1);

        Assert.Equal(1, cashDrawerService.OpenCallCount);
    }

    [Fact]
    public async Task Cash_payment_completion_shows_cash_drawer_failure_without_leaving_success_screen()
    {
        var cashDrawerService = new RecordingCashDrawerService
        {
            Result = new ReceiptPrintResult(false, "drawer offline")
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashDrawerService: cashDrawerService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash);

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => ReferenceEquals(viewModel.PaymentSuccess, viewModel.CurrentScreen) && cashDrawerService.OpenCallCount == 1);

        Assert.Equal(1, cashDrawerService.OpenCallCount);
        Assert.Equal("drawer offline", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_success_print_button_prints_current_receipt()
    {
        var printService = new RecordingReceiptPrintService();
        var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService(), printService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash);
        viewModel.PaymentSuccess.LoadFromOrder(order);

        viewModel.PaymentSuccess.PrintReceiptCommand.Execute(null);

        await WaitUntilAsync(() => printService.Calls.Count == 1);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(order.OrderGuid, call.OrderGuid);
        Assert.Equal(ReceiptPrintReason.Manual, call.Reason);
    }

    [Fact]
    public async Task Pos_terminal_print_last_receipt_command_uses_print_service()
    {
        var printService = new RecordingReceiptPrintService();
        var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService(), printService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        await viewModel.PosTerminal!.PrintLastReceiptCommand.ExecuteAsync(null);

        var call = Assert.Single(printService.Calls);
        Assert.Null(call.OrderGuid);
        Assert.Equal(ReceiptPrintReason.LastReceipt, call.Reason);
    }

    [Fact]
    public async Task Pos_terminal_open_cash_drawer_command_uses_cash_drawer_service()
    {
        var cashDrawerService = new RecordingCashDrawerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashDrawerService: cashDrawerService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        await viewModel.PosTerminal!.OpenCashDrawerCommand.ExecuteAsync(null);

        Assert.Equal(1, cashDrawerService.OpenCallCount);
        Assert.Equal("Cash drawer opened.", viewModel.PosTerminal.StatusMessage);
    }

    [Fact]
    public async Task Pos_terminal_exit_application_command_confirms_closes_customer_display_and_exits()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var exitService = new RecordingApplicationExitService();
        var confirmationDialog = new FakeConfirmationDialogService { ConfirmExitApplicationResult = true };
        var viewModel = CreateAuthorizedMainViewModel(
            customerDisplayWindow,
            applicationExitService: exitService,
            confirmationDialogService: confirmationDialog);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        await viewModel.PosTerminal!.ExitApplicationCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmationDialog.ConfirmExitApplicationCallCount);
        Assert.Equal(1, exitService.ExitCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Closed, customerDisplayWindow.LastSetMode);
    }

    [Fact]
    public async Task Pos_terminal_exit_application_command_does_not_exit_when_cancelled()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var exitService = new RecordingApplicationExitService();
        var confirmationDialog = new FakeConfirmationDialogService { ConfirmExitApplicationResult = false };
        var viewModel = CreateAuthorizedMainViewModel(
            customerDisplayWindow,
            applicationExitService: exitService,
            confirmationDialogService: confirmationDialog);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var setModeCallCount = customerDisplayWindow.SetModeCallCount;

        await viewModel.PosTerminal!.ExitApplicationCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmationDialog.ConfirmExitApplicationCallCount);
        Assert.Equal(0, exitService.ExitCallCount);
        Assert.Equal(setModeCallCount, customerDisplayWindow.SetModeCallCount);
    }

    [Fact]
    public async Task InitializeAsync_ShowsDeviceRegistrationWithoutWaitingForStoresOrCatalogLoad()
    {
        var allowCatalogLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deviceApi = new FakeDeviceApiClient();
        var catalog = new FakeCatalogRepository
        {
            BeforeLoadSellableItemsAsync = () => allowCatalogLoad.Task
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
        Assert.Equal(0, catalog.LoadSellableItemsCallCount);
    }

    [Fact]
    public async Task InitializeAsync_WaitsForStartupCatalogLoadBeforeShowingPos()
    {
        var index = new LocalSellableItemIndex();
        var allowCatalogLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "9528502522381")],
            BeforeLoadSellableItemsAsync = () => allowCatalogLoad.Task
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

        var initializeTask = viewModel.InitializeAsync(startupOptions);
        await WaitUntilAsync(() => catalog.LoadSellableItemsCallCount > 0);

        Assert.Equal(1, catalog.LoadSellableItemsCallCount);
        Assert.False(initializeTask.IsCompleted);
        Assert.NotSame(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        Assert.Empty(index.FindExactMatches("1042", "9528502522381"));

        allowCatalogLoad.SetResult();
        await initializeTask;

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Same(viewModel.PosTerminal, viewModel.CachedPosTerminalScreen);
        Assert.Same(viewModel.CashPayment, viewModel.CachedCashPaymentScreen);
        Assert.Null(viewModel.CachedSpecialProductsScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.False(viewModel.IsCashPaymentScreenActive);
        Assert.False(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
        Assert.Equal("SKU-001", Assert.Single(index.FindExactMatches("1042", "9528502522381")).ProductCode);

        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(1, catalog.LoadSellableItemsCallCount);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithAuthorizedDeviceAndConnectivityFailure_KeepsOfflinePosAvailable()
    {
        var authorizationState = new DeviceAuthorizationState();
        var connectivity = new FakeConnectivityApiClient
        {
            CheckOnlineException = new InvalidOperationException("API unavailable")
        };
        var viewModel = new MainViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            new FakeCatalogRepository { Items = [CreateItem("1042", "SKU-001", "9528502522381")] },
            new FakeCatalogSyncService(),
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            connectivity,
            new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
            new FakeDeviceApiClient(),
            new FakeDeviceFingerprintService(),
            authorizationState,
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            new FakeRawScannerService());
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.Equal("1042", viewModel.Session.StoreCode);
        Assert.Equal("POS-001", viewModel.Session.DeviceCode);
        Assert.False(viewModel.Session.IsOnline);
        Assert.NotNull(authorizationState.Current);
        Assert.Equal(1, connectivity.CheckOnlineCallCount);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithCatalogSyncFailure_KeepsLocalCatalogAndPosScreen()
    {
        var index = new LocalSellableItemIndex();
        var catalogSync = new FakeCatalogSyncService
        {
            FullSyncException = new InvalidOperationException("catalog API unavailable")
        };
        var viewModel = new MainViewModel(
            index,
            new PosCartService(),
            new CashCheckoutService(),
            new FakeLocalSchemaService(),
            new FakeSettingsRepository(),
            new FakeCatalogRepository { Items = [CreateItem("1042", "SKU-001", "9528502522381")] },
            catalogSync,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new FakeConnectivityApiClient(true),
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
        await WaitUntilAsync(() => catalogSync.FullSyncCallCount > 0);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.Equal("SKU-001", Assert.Single(index.FindExactMatches("1042", "9528502522381")).ProductCode);
        Assert.Equal(1, catalogSync.FullSyncCallCount);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithEmptyLocalCatalog_RunsInitialSyncWithoutStartupTimeout()
    {
        var shellCatalog = new RecordingShellCatalogService
        {
            SyncItems = [CreateItem("1042", "SKU-DOWNLOADED", "9528502522381")]
        };
        var viewModel = CreateMainViewModelWithShellCatalog(
            new FakeCatalogRepository(),
            shellCatalog,
            new FakeConnectivityApiClient(true));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await WaitUntilAsync(() => shellCatalog.SyncCallCount > 0);

        Assert.Equal(1, shellCatalog.SyncCallCount);
        Assert.False(shellCatalog.LastSyncCancellationToken.CanBeCanceled);
        Assert.Equal("SKU-DOWNLOADED", Assert.Single(viewModel.PosTerminal!.Matches).ProductCode);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithExistingLocalCatalog_RunsBackgroundRefreshWithoutStartupTimeout()
    {
        var shellCatalog = new RecordingShellCatalogService
        {
            SyncItems = [CreateItem("1042", "SKU-REFRESHED", "9528502522381")]
        };
        var viewModel = CreateMainViewModelWithShellCatalog(
            new FakeCatalogRepository { Items = [CreateItem("1042", "SKU-CACHED", "9528502522380")] },
            shellCatalog,
            new FakeConnectivityApiClient(true));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await WaitUntilAsync(() => shellCatalog.SyncCallCount > 0);

        Assert.Equal(1, shellCatalog.SyncCallCount);
        Assert.False(shellCatalog.LastSyncCancellationToken.CanBeCanceled);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithEmptyLocalCatalogSyncFailure_ShowsInitialDownloadFailure()
    {
        var shellCatalog = new RecordingShellCatalogService
        {
            SyncException = new InvalidOperationException("catalog API unavailable")
        };
        var viewModel = CreateMainViewModelWithShellCatalog(
            new FakeCatalogRepository(),
            shellCatalog,
            new FakeConnectivityApiClient(true));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await WaitUntilAsync(() => shellCatalog.SyncCallCount > 0 && viewModel.StatusMessage.Length > 0);

        Assert.Equal(1, shellCatalog.SyncCallCount);
        Assert.Contains("initial catalog", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("catalog API unavailable", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithRegistrationStoreLoadFailure_StaysOnRegistrationScreen()
    {
        var deviceApi = new FakeDeviceApiClient
        {
            GetStoresException = new InvalidOperationException("store API unavailable")
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
            new FakeLocalDeviceRepository(),
            deviceApi,
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

        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Same(viewModel.DeviceRegistration, viewModel.CurrentScreen);
        Assert.Equal("store API unavailable", viewModel.DeviceRegistration.StatusMessage);
        Assert.Equal(1, deviceApi.GetStoresCallCount);
        Assert.Null(viewModel.PosTerminal);
    }

    [Fact]
    public async Task InitializeAsync_LoadsSpecialProductsDataBeforeNavigatingToPos()
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
        Assert.Equal(1, catalog.LoadSpecialProductItemsCallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Same(viewModel.SpecialProducts, viewModel.CachedSpecialProductsScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.False(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_DoesNotWarmSpecialProductThumbnailsInBackground()
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

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Same(viewModel.SpecialProducts, viewModel.CachedSpecialProductsScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.Empty(loadedImages);
        Assert.DoesNotContain(expectedFirstPageImages, ImageCacheContainsForTests);

        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);
        await viewModel.SpecialProducts!.EnsureLoadedAsync();

        var firstPageItem = viewModel.SpecialProducts.PagedSpecialItems.First();
        Assert.IsType<BitmapImage>(
            converter.Convert(firstPageItem.ProductImage, typeof(BitmapSource), null, CultureInfo.InvariantCulture));
        Assert.Equal(1, loadedImages[firstPageItem.ProductImage!]);
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
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("S001", "SKU-RETURN", "690RET")]
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
        await WaitUntilAsync(() => index.FindExactMatches("S001", "690RET").Count == 1);
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
        Assert.Equal("No order loaded", returns.OrderSummaryText);
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
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "930110")]
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
            scanner);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal(PosTerminalViewModel.PageId, scanner.ActivePageId);

        await WaitUntilAsync(() => index.FindExactMatches("1042", "930110").Count == 1);
        scanner.Emit("930110");
        viewModel.ShowCashPaymentCommand.Execute(null);

        Assert.Same(viewModel.CashPayment, viewModel.CurrentScreen);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        Assert.True(viewModel.IsCashPaymentScreenActive);
        Assert.False(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
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
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items =
            [
                CreateItem("1042", "SKU-001", "930110"),
                CreateItem("1042", "SKU-002", "930111")
            ]
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
            scanner);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await WaitUntilAsync(() => index.FindExactMatches("1042", "930110").Count == 1);
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
    public async Task CashPaymentScreen_IsCachedAndResetEachTimeItIsOpened()
    {
        var scanner = new FakeRawScannerService();
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "930110")]
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
            scanner);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await WaitUntilAsync(() => index.FindExactMatches("1042", "930110").Count == 1);
        scanner.Emit("930110");

        viewModel.ShowCashPaymentCommand.Execute(null);
        var firstPaymentScreen = viewModel.CashPayment!;
        firstPaymentScreen.TenderAmountText = "5";
        await firstPaymentScreen.SelectCashCommand.ExecuteAsync(null);

        Assert.Same(firstPaymentScreen, viewModel.CurrentScreen);
        Assert.Same(firstPaymentScreen, viewModel.CachedCashPaymentScreen);
        Assert.Single(firstPaymentScreen.PaymentTenders);

        viewModel.ShowPosCommand.Execute(null);

        Assert.Same(firstPaymentScreen, viewModel.CachedCashPaymentScreen);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);

        viewModel.ShowCashPaymentCommand.Execute(null);

        Assert.Same(firstPaymentScreen, viewModel.CashPayment);
        Assert.Same(firstPaymentScreen, viewModel.CurrentScreen);
        Assert.True(viewModel.IsCashPaymentScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
        Assert.Empty(firstPaymentScreen.PaymentTenders);
        Assert.True(firstPaymentScreen.IsCashSelected);
        Assert.Equal(string.Empty, firstPaymentScreen.TenderAmountText);
        Assert.Null(scanner.ActivePageId);

        firstPaymentScreen.BackToPosCommand.Execute(null);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
    }

    [Fact]
    public async Task BeginDeviceReregistration_ClearsCachedCashPaymentScreen()
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
            new FakeSyncQueueRepository(),
            new LocalizationService(),
            new FakeCustomerDisplayWindowService(),
            new FakeRawScannerService());

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.NotNull(viewModel.CachedCashPaymentScreen);

        await InvokePrivateTaskAsync(viewModel, "BeginDeviceReregistrationAsync");

        Assert.Null(viewModel.CachedCashPaymentScreen);
        Assert.Null(viewModel.CashPayment);
        Assert.True(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.False(viewModel.IsCashPaymentScreenActive);
    }

    [Fact]
    public async Task Settings_ReregisterDeviceCommand_OpensDialogAndLoadsStores()
    {
        var deviceApi = new FakeDeviceApiClient
        {
            Stores =
            [
                new StoreSelectionItem("1042", "Old Store", true),
                new StoreSelectionItem("2042", "New Store", true)
            ]
        };
        var viewModel = CreateAuthorizedMainViewModelWithSettings(deviceApiClient: deviceApi);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowSettingsCommand.ExecuteAsync(null);
        var settings = Assert.IsType<SettingsViewModel>(viewModel.CurrentScreen);

        await settings.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Same(settings, viewModel.CurrentScreen);
        Assert.Equal(1, deviceApi.GetStoresCallCount);
        Assert.DoesNotContain(viewModel.DeviceRegistration.Stores, store => store.StoreCode == "1042");
        var store = Assert.Single(viewModel.DeviceRegistration.Stores);
        Assert.Equal("2042", store.StoreCode);
        Assert.Null(viewModel.DeviceRegistration.SelectedStore);
    }

    [Fact]
    public async Task Settings_ReregisterDeviceCommand_NotifiesDeviceRegistrationForDialogBinding()
    {
        var deviceApi = new FakeDeviceApiClient
        {
            Stores =
            [
                new StoreSelectionItem("1042", "Old Store", true),
                new StoreSelectionItem("2042", "New Store", true)
            ]
        };
        var viewModel = CreateAuthorizedMainViewModelWithSettings(deviceApiClient: deviceApi);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowSettingsCommand.ExecuteAsync(null);
        var settings = Assert.IsType<SettingsViewModel>(viewModel.CurrentScreen);
        changedProperties.Clear();

        await settings.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.Contains(nameof(MainViewModel.DeviceRegistration), changedProperties);
        Assert.True(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.NotNull(viewModel.DeviceRegistration);
        var store = Assert.Single(viewModel.DeviceRegistration.Stores);
        Assert.Equal("2042", store.StoreCode);
        Assert.True(viewModel.DeviceRegistration.CancelCommand.CanExecute(null));

        changedProperties.Clear();
        viewModel.DeviceRegistration.CancelCommand.Execute(null);

        Assert.Contains(nameof(MainViewModel.DeviceRegistration), changedProperties);
        Assert.False(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Null(viewModel.DeviceRegistration);
    }

    [Fact]
    public async Task Settings_ReregisterDeviceCommand_CancelWhileLoadingStoresClosesDialog()
    {
        var pendingStores = new TaskCompletionSource<IReadOnlyList<StoreSelectionItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deviceApi = new FakeDeviceApiClient
        {
            PendingStoresResult = pendingStores
        };
        var viewModel = CreateAuthorizedMainViewModelWithSettings(deviceApiClient: deviceApi);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowSettingsCommand.ExecuteAsync(null);
        var settings = Assert.IsType<SettingsViewModel>(viewModel.CurrentScreen);

        var openTask = settings.ReregisterDeviceCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsDeviceReregistrationDialogOpen && viewModel.DeviceRegistration is not null);

        Assert.True(viewModel.DeviceRegistration!.CancelCommand.CanExecute(null));

        viewModel.DeviceRegistration.CancelCommand.Execute(null);

        Assert.False(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Null(viewModel.DeviceRegistration);
        Assert.Same(settings, viewModel.CurrentScreen);

        pendingStores.SetResult(
        [
            new StoreSelectionItem("1042", "Old Store", true),
            new StoreSelectionItem("2042", "New Store", true)
        ]);
        await openTask;

        Assert.False(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Null(viewModel.DeviceRegistration);
        Assert.Same(settings, viewModel.CurrentScreen);
    }

    [Fact]
    public async Task Settings_ReregisterDeviceCommand_WithOnlyCurrentStoreShowsEmptyStateAndCanCancel()
    {
        var deviceApi = new FakeDeviceApiClient
        {
            Stores = [new StoreSelectionItem("1042", "Old Store", true)]
        };
        var viewModel = CreateAuthorizedMainViewModelWithSettings(deviceApiClient: deviceApi);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowSettingsCommand.ExecuteAsync(null);
        var settings = Assert.IsType<SettingsViewModel>(viewModel.CurrentScreen);

        await settings.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Empty(viewModel.DeviceRegistration.Stores);
        Assert.Null(viewModel.DeviceRegistration.SelectedStore);
        Assert.False(viewModel.DeviceRegistration.RegisterCommand.CanExecute(null));
        Assert.Contains("No other", viewModel.DeviceRegistration.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stores", viewModel.DeviceRegistration.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.DeviceRegistration.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task Settings_ReregisterDeviceCommand_WhenSyncPending_ShowsBlockedReason()
    {
        var syncQueue = new FakeSyncQueueRepository { Overview = new SyncQueueOverview(1, 0, 0, null) };
        var viewModel = CreateAuthorizedMainViewModelWithSettings(syncQueueRepository: syncQueue);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowSettingsCommand.ExecuteAsync(null);
        var settings = Assert.IsType<SettingsViewModel>(viewModel.CurrentScreen);

        await settings.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Null(viewModel.DeviceRegistration);
        Assert.Same(settings, viewModel.CurrentScreen);
        Assert.Contains("pending order sync", settings.StatusMessage, StringComparison.OrdinalIgnoreCase);
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
        var index = new LocalSellableItemIndex();
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
            scanner);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await WaitUntilAsync(() => index.FindExactMatches("1042", "930110").Count == 1);
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
        await WaitUntilAsync(() => viewModel.StatusMessage.Contains("catalog load failed", StringComparison.Ordinal));

        Assert.Equal(1, catalog.LoadSellableItemsCallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Contains("catalog load failed", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_WhenStartupCatalogLoadIsCanceled_StillShowsPos()
    {
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "319844731768")],
            LoadSellableItemsException = new OperationCanceledException("catalog load canceled")
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

        Assert.Equal(1, catalog.LoadSellableItemsCallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.Empty(index.FindExactMatches("1042", "319844731768"));
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithSecondDisplay_KeepsCustomerDisplayWindowClosed()
    {
        using var logs = new ConsoleLogCapture();
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(0, customerDisplayWindow.PrewarmCallCount);
        Assert.Equal(0, customerDisplayWindow.WindowCreationCount);
        Assert.Equal(0, customerDisplayWindow.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Closed, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
        Assert.False(viewModel.IsCustomerDisplayOpen);
        Assert.Contains(logs.Lines, line => line.Contains("[CustomerDisplay]") && line.Contains("startup prewarm skipped") && line.Contains("reason=auto-open-disabled"));
        Assert.Contains(logs.Lines, line => line.Contains("[CustomerDisplay]") && line.Contains("post-show open skipped") && line.Contains("reason=auto-open-disabled"));
    }

    [Fact]
    public async Task InitializeAsync_PreloadsSpecialProductsDataBeforeMainWindowShown()
    {
        using var logs = new ConsoleLogCapture();
        var specialProductsWorkflow = new FakeSpecialProductsWorkflowService
        {
            PreloadResult = new SpecialProductsLoadResult("1042", [CreateItem("1042", "SKU-SP", "930001")])
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            specialProductsWorkflowService: specialProductsWorkflow);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal(1, specialProductsWorkflow.PreloadCallCount);
        Assert.Equal("1042", specialProductsWorkflow.LastPreloadStoreCode);
        Assert.Single(viewModel.SpecialProducts!.SpecialItems);
        Assert.Contains(logs.Lines, line => line.Contains("[SpecialProducts]") && line.Contains("startup data preload completed"));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("startup thumbnail preload completed"));
    }

    [Fact]
    public async Task InitializeAsync_SpecialProductsPreloadFailure_DoesNotBlockMainWindow()
    {
        using var logs = new ConsoleLogCapture();
        var specialProductsWorkflow = new FakeSpecialProductsWorkflowService
        {
            PreloadException = new InvalidOperationException("special preload failed")
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            specialProductsWorkflowService: specialProductsWorkflow);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal(1, specialProductsWorkflow.PreloadCallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.Contains(logs.Lines, line => line.Contains("[SpecialProducts]") && line.Contains("preload failed"));
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WhenCalledTwice_DoesNotAutoOpenCustomerDisplayWindow()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(0, customerDisplayWindow.PrewarmCallCount);
        Assert.Equal(0, customerDisplayWindow.WindowCreationCount);
        Assert.Equal(0, customerDisplayWindow.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Closed, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
        Assert.False(viewModel.IsCustomerDisplayOpen);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_DoesNotPreloadSpecialProductsHome()
    {
        using var logs = new ConsoleLogCapture();
        var specialProductsWorkflow = new FakeSpecialProductsWorkflowService
        {
            PreloadResult = new SpecialProductsLoadResult("1042", [CreateItem("1042", "SKU-SP", "930001")])
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            specialProductsWorkflowService: specialProductsWorkflow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        var preloadCallCountAfterInitialize = specialProductsWorkflow.PreloadCallCount;
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(1, preloadCallCountAfterInitialize);
        Assert.Equal(preloadCallCountAfterInitialize, specialProductsWorkflow.PreloadCallCount);
        Assert.Contains(logs.Lines, line => line.Contains("[SpecialProducts]") && line.Contains("startup home preload skipped"));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("startup thumbnail preload completed"));
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithSingleDisplay_DoesNotAttemptCustomerDisplayWindow()
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

        Assert.Equal(0, customerDisplayWindow.PrewarmCallCount);
        Assert.Equal(0, customerDisplayWindow.WindowCreationCount);
        Assert.Equal(0, customerDisplayWindow.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Closed, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
        Assert.False(viewModel.IsCustomerDisplayOpen);
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
    public async Task SetCustomerDisplayWindowMode_PreservesManualNormalFullscreenAndCloseSemantics()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        viewModel.SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Normal, owner: null);

        Assert.Equal(CustomerDisplayWindowMode.Normal, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Normal, viewModel.CustomerDisplayWindowMode);
        Assert.True(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("Customer display opened in a normal window on the second display.", viewModel.StatusMessage);

        viewModel.SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Fullscreen, owner: null);

        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, viewModel.CustomerDisplayWindowMode);
        Assert.True(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("Customer display opened full screen on the second display.", viewModel.StatusMessage);

        viewModel.SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Closed, owner: null);

        Assert.Equal(3, customerDisplayWindow.SetModeCallCount);
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
        viewModel.SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Fullscreen, owner: null);

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
        Assert.Contains("pending order sync", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetrySyncOrderCommand_WithFailedOrder_RetriesSingleOrderAndRefreshesSyncCenter()
    {
        var orderGuid = Guid.NewGuid();
        var item = CreateSyncQueueItem(orderGuid, "Failed");
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(0, 1, 0, "network down"),
            ActiveItems = [item]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecuteOneResult = new OrderUploadExecutionResult(1, 1, 0),
            OnExecuteOne = _ =>
            {
                syncQueue.Overview = new SyncQueueOverview(0, 0, 0, null);
                syncQueue.ActiveItems = [];
            }
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.True(viewModel.RetrySyncOrderCommand.CanExecute(item));

        await viewModel.RetrySyncOrderCommand.ExecuteAsync(item);

        Assert.Equal(orderGuid, uploadExecution.LastExecuteOneOrderGuid);
        Assert.Equal(0, viewModel.PendingUploadCount);
        Assert.Equal(0, viewModel.FailedUploadCount);
        Assert.Empty(viewModel.SyncCenterOrders);
        Assert.Contains("1", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryAllSyncOrdersCommand_IsDisabledWhenOnlySyncingOrdersExist()
    {
        var item = CreateSyncQueueItem(Guid.NewGuid(), "Syncing");
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: new FakeSyncQueueRepository
            {
                Overview = new SyncQueueOverview(0, 0, 1, null),
                ActiveItems = [item]
            });

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.False(viewModel.RetryAllSyncOrdersCommand.CanExecute(null));
        Assert.False(viewModel.RetrySyncOrderCommand.CanExecute(item));
    }

    [Fact]
    public async Task RetryAllSyncOrdersCommand_WhenRetryClearsFailures_AllowsDeviceReregistration()
    {
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(0, 1, 0, "network down"),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Failed")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingResult = new OrderUploadExecutionResult(1, 1, 0),
            OnExecutePending = () =>
            {
                syncQueue.Overview = new SyncQueueOverview(0, 0, 0, null);
                syncQueue.ActiveItems = [];
            }
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        Assert.True(viewModel.RetryAllSyncOrdersCommand.CanExecute(null));

        await viewModel.RetryAllSyncOrdersCommand.ExecuteAsync(null);
        await viewModel.PosTerminal!.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Equal(1, uploadExecution.ExecutePendingCallCount);
    }

    [Fact]
    public async Task RetryAllSyncOrdersCommand_WhenRetryFails_KeepsFailedCountAndError()
    {
        var item = CreateSyncQueueItem(Guid.NewGuid(), "Failed", "network down");
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(0, 1, 0, "network down"),
            ActiveItems = [item]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingResult = new OrderUploadExecutionResult(1, 0, 1)
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        await viewModel.RetryAllSyncOrdersCommand.ExecuteAsync(null);

        Assert.Equal(1, viewModel.FailedUploadCount);
        Assert.Equal("network down", viewModel.LastOrderSyncErrorText);
        Assert.Single(viewModel.SyncCenterOrders);
        Assert.Contains("0", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("1", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_online_check_auto_retries_pending_orders_and_refreshes_sync_center()
    {
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(1, 0, 0, null),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Pending")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingResult = new OrderUploadExecutionResult(1, 1, 0),
            OnExecutePending = () =>
            {
                syncQueue.Overview = new SyncQueueOverview(0, 0, 0, null);
                syncQueue.ActiveItems = [];
            }
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(true));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(1, uploadExecution.ExecutePendingCallCount);
        Assert.Equal(0, viewModel.PendingUploadCount);
        Assert.Empty(viewModel.SyncCenterOrders);
    }

    [Fact]
    public async Task Connectivity_refresh_auto_retries_when_backend_changes_from_offline_to_online()
    {
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(1, 0, 0, null),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Pending")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingResult = new OrderUploadExecutionResult(1, 1, 0),
            OnExecutePending = () =>
            {
                syncQueue.Overview = new SyncQueueOverview(0, 0, 0, null);
                syncQueue.ActiveItems = [];
            }
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(false, true));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await InvokeRefreshOnlineStateAsync(viewModel);
        await InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);

        Assert.Equal(1, uploadExecution.ExecutePendingCallCount);
        Assert.True(viewModel.Session.IsOnline);
        Assert.Equal(0, viewModel.PendingUploadCount);
    }

    [Fact]
    public async Task Connectivity_refresh_auto_retries_each_online_cycle_without_overwriting_status()
    {
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(1, 0, 0, null),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Pending")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingResult = new OrderUploadExecutionResult(1, 1, 0)
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(true, true));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.StatusMessage = "keep this status";
        await InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);
        await InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);

        Assert.Equal(2, uploadExecution.ExecutePendingCallCount);
        Assert.Equal("keep this status", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Connectivity_refresh_skips_auto_retry_while_manual_retry_is_running()
    {
        var releaseManualRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualRetryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(1, 0, 0, null),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Pending")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            PendingExecutionStarted = manualRetryStarted,
            ReleasePendingExecution = releaseManualRetry
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(true));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var manualRetryTask = viewModel.RetryAllSyncOrdersCommand.ExecuteAsync(null);
        await manualRetryStarted.Task;

        await InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);
        releaseManualRetry.SetResult();
        await manualRetryTask;

        Assert.Equal(1, uploadExecution.ExecutePendingCallCount);
    }

    [Fact]
    public async Task Connectivity_refresh_keeps_sync_snapshot_when_auto_retry_fails()
    {
        var item = CreateSyncQueueItem(Guid.NewGuid(), "Failed", "network down");
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(0, 1, 0, "network down"),
            ActiveItems = [item]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingException = new InvalidOperationException("still offline")
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(true));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);

        Assert.Equal(1, uploadExecution.ExecutePendingCallCount);
        Assert.Equal(1, viewModel.FailedUploadCount);
        Assert.Equal("network down", viewModel.LastOrderSyncErrorText);
        Assert.Single(viewModel.SyncCenterOrders);
    }

    [Fact]
    public async Task Connectivity_refresh_without_auto_retry_only_updates_online_state()
    {
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(1, 0, 0, null),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Pending")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(true));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await InvokeRefreshOnlineStateAsync(viewModel);

        Assert.True(viewModel.Session.IsOnline);
        Assert.Equal(0, uploadExecution.ExecutePendingCallCount);
    }

    [Fact]
    public async Task Connectivity_refresh_swallows_auto_retry_snapshot_refresh_failure()
    {
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(1, 0, 0, null),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Pending")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingException = new InvalidOperationException("upload failed"),
            OnBeforeExecutePendingException = () => syncQueue.ThrowOnRead = true
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(true));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var isOnline = await InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);

        Assert.True(isOnline);
        Assert.Equal(1, uploadExecution.ExecutePendingCallCount);
    }

    [Fact]
    public async Task Card_payment_recovery_checking_result_allows_next_recovery_check()
    {
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Checking, "checking")),
            Task.FromResult(CardPaymentRecoveryResult.None));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery);

        await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: true);
        await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: true);

        Assert.Equal(2, recovery.CallCount);
    }

    [Fact]
    public async Task Card_payment_recovery_concurrent_checks_share_inflight_task()
    {
        var recoveryResult = new TaskCompletionSource<CardPaymentRecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = new FakeCardPaymentRecoveryService(recoveryResult.Task);
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery);

        var first = InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: true);
        var second = InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: true);
        while (recovery.CallCount == 0)
        {
            await Task.Yield();
        }

        recoveryResult.SetResult(CardPaymentRecoveryResult.None);
        await Task.WhenAll(first, second);

        Assert.Equal(1, recovery.CallCount);
    }

    [Fact]
    public async Task Card_payment_recovery_check_does_not_run_when_opening_payment_with_non_empty_cart()
    {
        var cart = new PosCartService();
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Checking, "checking")),
            Task.FromResult(CardPaymentRecoveryResult.None));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cart: cart,
            cardPaymentRecoveryService: recovery);

        await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: true);
        cart.AddItem(CreateItem("1042", "SKU-CURRENT", "930CURRENT"));

        viewModel.ShowCashPaymentCommand.Execute(null);

        Assert.Equal(1, recovery.CallCount);
        Assert.Equal("Payment", viewModel.ActivePageTitleText);
    }

    [Fact]
    public async Task Card_payment_recovery_completed_during_startup_prints_card_receipt()
    {
        var printService = new RecordingReceiptPrintService();
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card);
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.OrderCompleted,
                "Recovered approved payment.",
                order)));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            receiptPrintService: printService,
            cardPaymentRecoveryService: recovery);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var recovered = await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: false);

        Assert.True(recovered);
        Assert.Same(viewModel.PaymentSuccess, viewModel.CurrentScreen);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(order.OrderGuid, call.OrderGuid);
        Assert.Equal(ReceiptPrintReason.CardAuto, call.Reason);
        Assert.True(viewModel.IsCardRecoveryResultDialogOpen);
        Assert.NotNull(viewModel.CardRecoveryResultDialog);
        Assert.Equal("Card transaction recovered successfully", viewModel.CardRecoveryResultDialog!.Title);
        Assert.True(viewModel.CardRecoveryResultDialog.CanPrintReceipt);
        Assert.Equal("Print receipt", viewModel.CardRecoveryResultDialog.PrintButtonText);
        Assert.True(viewModel.CardRecoveryResultDialog.HasReceiptPreview);

        await viewModel.PrintRecoveredReceiptCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => printService.Calls.Count == 2);
        Assert.Equal(ReceiptPrintReason.CardAuto, printService.Calls[1].Reason);
    }

    [Fact]
    public async Task Card_payment_recovery_draft_restored_during_startup_keeps_pos_screen_and_surfaces_status()
    {
        var cart = new PosCartService();
        var recovery = new FakeCardPaymentRecoveryService((recoveredCart, _, _) =>
        {
            // 模拟恢复服务在返回结果前已经把草稿购物车恢复到当前会话。
            recoveredCart.AddItem(CreateItem("1042", "SKU-RECOVER-STARTUP", "930RECOVERSTARTUP"));
            return Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.DraftRestored,
                "Recovered draft during startup."));
        });
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery,
            cart: cart);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var recovered = await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: false);

        Assert.True(recovered);
        Assert.Equal(1, recovery.CallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.False(viewModel.IsCashPaymentScreenActive);
        Assert.Equal("Recovered draft during startup.", viewModel.StatusMessage);
        Assert.Single(cart.Lines);
        Assert.True(viewModel.IsCardRecoveryResultDialogOpen);
        Assert.NotNull(viewModel.CardRecoveryResultDialog);
        Assert.Equal("Previous card transaction was not completed", viewModel.CardRecoveryResultDialog!.Title);
        Assert.False(viewModel.CardRecoveryResultDialog.CanPrintReceipt);
    }

    [Fact]
    public async Task Card_payment_recovery_unknown_result_opens_failure_dialog_without_print_button()
    {
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                "Manual review required.",
                DialogDetails: new CardPaymentRecoveryDialogDetails(
                    "session-review",
                    "txn-review",
                    "TM",
                    "OPERATOR TIMEOUT",
                    1.25m,
                    new DateTimeOffset(2026, 6, 10, 9, 45, 0, TimeSpan.Zero)))));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var recovered = await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: false);

        Assert.False(recovered);
        Assert.True(viewModel.IsCardRecoveryResultDialogOpen);
        var dialog = viewModel.CardRecoveryResultDialog;
        Assert.NotNull(dialog);
        Assert.Equal("Previous card transaction was not successful", dialog.Title);
        Assert.Equal("session-review", dialog.SessionId);
        Assert.Equal("txn-review", dialog.TxnRef);
        Assert.Equal("TM", dialog.ResponseCode);
        Assert.False(dialog.CanPrintReceipt);
    }

    [Fact]
    public async Task Card_payment_recovery_draft_restored_is_not_checked_when_opening_payment()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-CURRENT-PAYMENT", "930CURRENTPAYMENT"));
        var recovery = new FakeCardPaymentRecoveryService((recoveredCart, _, _) =>
        {
            // 模拟恢复服务把待继续支付的购物车恢复回来，界面应直接收口到支付页。
            recoveredCart.AddItem(CreateItem("1042", "SKU-RECOVER-PAYMENT", "930RECOVERPAYMENT"));
            return Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.DraftRestored,
                "Recovered draft for payment."));
        });
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery,
            cart: cart);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        viewModel.ShowCashPaymentCommand.Execute(null);

        Assert.Same(viewModel.CashPayment, viewModel.CurrentScreen);
        Assert.True(viewModel.IsCashPaymentScreenActive);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        Assert.Equal(0, recovery.CallCount);
        Assert.Equal("Payment", viewModel.ActivePageTitleText);
        Assert.Single(cart.Lines);
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
        viewModel.DeviceRegistration!.SelectedStore = viewModel.DeviceRegistration.Stores.Single(store => store.StoreCode == "2042");
        await viewModel.DeviceRegistration!.RegisterCommand.ExecuteAsync(null);

        Assert.Null(authorizationState.Current);
        Assert.True(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Equal("POS-NEW", viewModel.DeviceRegistration.DeviceCode);
        Assert.Equal("2042", deviceApi.LastReregisterRequest?.TargetStoreCode);
    }

    [Fact]
    public async Task ReregisterDevice_CancelWhileSubmittingKeepsCurrentAuthorization()
    {
        var authorizationState = new DeviceAuthorizationState();
        var pendingReregister = new TaskCompletionSource<DeviceReregisterResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deviceApi = new FakeDeviceApiClient
        {
            Stores =
            [
                new StoreSelectionItem("1042", "Old Store", true),
                new StoreSelectionItem("2042", "New Store", true)
            ],
            PendingReregisterResponse = pendingReregister
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
        var originalAuthorization = Assert.IsType<DeviceAuthorizationContext>(authorizationState.Current);

        await viewModel.PosTerminal!.ReregisterDeviceCommand.ExecuteAsync(null);
        viewModel.DeviceRegistration!.SelectedStore = viewModel.DeviceRegistration.Stores.Single(store => store.StoreCode == "2042");
        var submitTask = viewModel.DeviceRegistration.RegisterCommand.ExecuteAsync(null);
        await deviceApi.WaitForReregisterStartedAsync();

        Assert.True(viewModel.DeviceRegistration.CancelCommand.CanExecute(null));

        viewModel.DeviceRegistration.CancelCommand.Execute(null);

        Assert.False(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Null(viewModel.DeviceRegistration);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);

        pendingReregister.SetResult(new DeviceReregisterResponse("POS-NEW", "2042", "New Store", -1, false, "Pending approval"));
        await submitTask;

        Assert.Same(originalAuthorization, authorizationState.Current);
        Assert.NotNull(viewModel.PosTerminal);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.False(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Null(viewModel.DeviceRegistration);
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

    private static MainViewModel CreateAuthorizedMainViewModel(
        FakeCustomerDisplayWindowService customerDisplayWindow,
        IReceiptPrintService? receiptPrintService = null,
        FakeSyncQueueRepository? syncQueueRepository = null,
        IOrderUploadExecutionService? orderUploadExecutionService = null,
        ICashDrawerService? cashDrawerService = null,
        IApplicationExitService? applicationExitService = null,
        IConfirmationDialogService? confirmationDialogService = null,
        IConnectivityApiClient? connectivityApiClient = null,
        ISpecialProductsWorkflowService? specialProductsWorkflowService = null,
        ICardPaymentRecoveryService? cardPaymentRecoveryService = null,
        PosCartService? cart = null)
    {
        var priceIndex = new LocalSellableItemIndex();
        var effectiveCart = cart ?? new PosCartService();
        var checkout = new CashCheckoutService();
        var catalogRepository = new FakeCatalogRepository();
        var syncQueue = syncQueueRepository ?? new FakeSyncQueueRepository();
        var orderRepository = new FakeLocalOrderRepository();
        var localization = new LocalizationService();
        var deviceRepository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprintService = new FakeDeviceFingerprintService();
        return new MainViewModel(
            priceIndex,
            effectiveCart,
            checkout,
            new FakeLocalSchemaService(),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalogRepository, new FakeCatalogSyncService()),
            catalogRepository,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            connectivityApiClient ?? new FakeConnectivityApiClient(),
            new MainShellStartupService(deviceRepository, fingerprintService, new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(customerDisplayWindow),
            new FakeRawScannerService(),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), deviceRepository, fingerprintService),
            specialProductsWorkflowService ?? new SpecialProductsWorkflowService(priceIndex, effectiveCart, catalogRepository, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                effectiveCart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync),
            receiptPrintService: receiptPrintService,
            orderUploadExecutionService: orderUploadExecutionService,
            cashDrawerService: cashDrawerService,
            applicationExitService: applicationExitService,
            confirmationDialogService: confirmationDialogService,
            cardPaymentRecoveryService: cardPaymentRecoveryService);
    }

    private static MainViewModel CreateMainViewModelWithShellCatalog(
        FakeCatalogRepository catalogRepository,
        IShellCatalogService shellCatalogService,
        IConnectivityApiClient connectivityApiClient)
    {
        var localization = new LocalizationService();
        var priceIndex = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var deviceRepository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprintService = new FakeDeviceFingerprintService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();

        return new MainViewModel(
            priceIndex,
            cart,
            checkout,
            new FakeLocalSchemaService(),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            shellCatalogService,
            catalogRepository,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            connectivityApiClient,
            new MainShellStartupService(deviceRepository, fingerprintService, new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new FakeRawScannerService(),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), deviceRepository, fingerprintService),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepository, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));
    }

    private static async Task<bool> InvokeRecoverCardPaymentAttemptAsync(
        MainViewModel viewModel,
        bool navigateToPaymentOnDraft)
    {
        var method = typeof(MainViewModel).GetMethod(
            "RecoverCardPaymentAttemptAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(bool)],
            modifiers: null);
        Assert.NotNull(method);
        var task = (Task<bool>)method!.Invoke(viewModel, [navigateToPaymentOnDraft])!;
        return await task;
    }

    private static async Task<bool> InvokeRefreshOnlineStateAsync(MainViewModel viewModel)
    {
        var method = typeof(MainViewModel).GetMethod(
            "RefreshOnlineStateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(CancellationToken)],
            modifiers: null);
        Assert.NotNull(method);
        var task = (Task<bool>)method!.Invoke(viewModel, [CancellationToken.None])!;
        return await task;
    }

    private static async Task<bool> InvokeRefreshOnlineStateAsync(MainViewModel viewModel, bool autoRetryOrders)
    {
        var method = typeof(MainViewModel).GetMethod(
            "RefreshOnlineStateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(CancellationToken), typeof(bool)],
            modifiers: null);
        Assert.NotNull(method);
        var task = (Task<bool>)method!.Invoke(viewModel, [CancellationToken.None, autoRetryOrders])!;
        return await task;
    }

    private static MainViewModel CreateAuthorizedMainViewModelWithPaymentWorkflow(
        PosCartService cart,
        CashCheckoutService checkout,
        ILocalOrderRepository orderRepository,
        ISyncQueueRepository syncQueue,
        ICardTerminalClient cardTerminalClient)
    {
        var priceIndex = new LocalSellableItemIndex();
        var catalogRepository = new FakeCatalogRepository();
        var localization = new LocalizationService();
        var workflow = new CashPaymentWorkflowService(
            checkout,
            orderRepository,
            syncQueue,
            cardTerminalClient: cardTerminalClient);

        return new MainViewModel(
            priceIndex,
            cart,
            checkout,
            new FakeLocalSchemaService(),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalogRepository, new FakeCatalogSyncService()),
            catalogRepository,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new FakeConnectivityApiClient(),
            new MainShellStartupService(
                new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
                new FakeDeviceFingerprintService(),
                new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new FakeRawScannerService(),
            new ReceiptQueryService(orderRepository),
            workflow,
            new DeviceRegistrationWorkflowService(
                new FakeDeviceApiClient(),
                new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
                new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepository, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));
    }

    private static MainViewModel CreateAuthorizedMainViewModelWithSettings(
        FakeDeviceApiClient? deviceApiClient = null,
        FakeSyncQueueRepository? syncQueueRepository = null)
    {
        var settingsRepository = new FakeSettingsRepository();
        var catalogRepository = new FakeCatalogRepository();
        var orderRepository = new FakeLocalOrderRepository();
        var deviceRepository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprintService = new FakeDeviceFingerprintService();
        var deviceApi = deviceApiClient ?? new FakeDeviceApiClient();
        var localization = new LocalizationService();
        var cart = new PosCartService();
        var priceIndex = new LocalSellableItemIndex();
        var checkout = new CashCheckoutService();
        var syncQueue = syncQueueRepository ?? new FakeSyncQueueRepository();

        return new MainViewModel(
            priceIndex,
            cart,
            checkout,
            new FakeLocalSchemaService(),
            new ShellCultureService(localization, settingsRepository),
            new ShellCatalogService(priceIndex, catalogRepository, new FakeCatalogSyncService()),
            catalogRepository,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new FakeConnectivityApiClient(),
            new MainShellStartupService(deviceRepository, fingerprintService, new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new FakeRawScannerService(),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            new DeviceRegistrationWorkflowService(deviceApi, deviceRepository, fingerprintService),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepository, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync),
            cardTerminalSetupService: new FakeCardTerminalSetupService());
    }

    private static SyncQueueListItem CreateSyncQueueItem(Guid entityId, string status, string? errorMessage = null)
    {
        return new SyncQueueListItem(
            entityId,
            "Order",
            status,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            errorMessage,
            12.30m);
    }

    private static LocalOrder CreateReceiptPrintOrder(params PaymentMethodKind[] paymentMethods)
    {
        var orderGuid = Guid.NewGuid();
        var methods = paymentMethods.Length == 0 ? [PaymentMethodKind.Cash] : paymentMethods;
        var paymentAmount = decimal.Round(10m / methods.Length, 2, MidpointRounding.AwayFromZero);
        return new LocalOrder(
            orderGuid,
            "1042",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.UtcNow,
            10m,
            0m,
            10m,
            [
                new LocalOrderLine(
                    Guid.NewGuid(),
                    "SKU-001",
                    null,
                    "Receipt Item",
                    "930110",
                    "ITEM-1",
                    1m,
                    10m,
                    0m,
                    10m,
                    PriceSourceKind.StoreRetailPrice)
            ],
            methods
                .Select(paymentMethod => new LocalPayment(
                    Guid.NewGuid(),
                    paymentMethod,
                    paymentAmount,
                    paymentMethod == PaymentMethodKind.Card ? "CARD-123" : null,
                    paymentMethod == PaymentMethodKind.Card
                        ? [
                            new CardTransactionDto(
                                "Linkly",
                                "TXN-1",
                                "AUTH-1",
                                "VISA",
                                411111,
                                "****1111",
                                "M1",
                                "00",
                                "APPROVED",
                                "123456",
                                DateTimeOffset.UtcNow,
                                paymentAmount,
                                "APPROVED CARD RECEIPT")
                        ]
                        : null))
                .ToArray());
    }

    private static void InvokePaymentCompleted(MainViewModel viewModel, LocalOrder order)
    {
        var method = typeof(MainViewModel).GetMethod("OnPaymentCompleted", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(viewModel, [null, new PaymentCompletedEventArgs(order, order.ActualAmount, 0m)]);
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

    private static async Task InvokePrivateTaskAsync(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(target, null) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static void ClearImageCacheForTests()
    {
        ClearConcurrentDictionaryField("Cache");
        ClearConcurrentDictionaryField("FailedCache");
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

    private sealed class FakeCardTerminalSetupService : ICardTerminalSetupService
    {
        public Task<CardTerminalConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CardTerminalConfiguration.Default);
        }

        public Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<SquareLocationOption>> ListSquareLocationsAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SquareLocationOption>>([]);
        }

        public Task<IReadOnlyList<SquareDeviceOption>> ListSquareDevicesAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SquareDeviceOption>>([]);
        }

        public Task<IReadOnlyList<SquareDeviceCodeOption>> ListSquareDeviceCodesAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SquareDeviceCodeOption>>([]);
        }

        public Task<SquareDeviceCodeOption> CreateSquareDeviceCodeAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            string name,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SquareDeviceCodeOption> GetSquareDeviceCodeAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string deviceCodeId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SaveSquareAsync(
            CardTerminalConfiguration configuration,
            string? squareAccessToken,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveLinklyAsync(
            CardTerminalConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<LinklyConnectionTestResult> TestLinklyConnectionAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(false, "not tested"));
        }

        public Task<LinklyConnectionTestResult> PairLinklyCloudAsync(
            CardTerminalEnvironment environment,
            string pairCode,
            string? username,
            string? password,
            bool syncBackendTerminalCredential = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(false, "not tested"));
        }

        public Task<LinklyCloudCredentialSettings> LoadLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyCloudCredentialSettings(null, null, false));
        }

        public Task SaveLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            string username,
            string password,
            bool syncBackendCredential = false,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<LinklyConnectionTestResult> TestLinklyCloudConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(false, "not tested"));
        }

        public Task<LinklyConnectionTestResult> TestLinklyCloudBackendConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(false, "not tested"));
        }

        public Task<LinklyConnectionTestResult> TestLinklyCloudBackendTransactionStatusAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(false, "not tested"));
        }

        public Task<bool> HasLinklyCloudSecretAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task SaveLinklyCloudAsync(
            CardTerminalConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ApprovedCardTerminalClient(string reference) : ICardTerminalClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(true, reference, AuthorizedAmount: amount));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(true, $"REFUND:{originalReference}", AuthorizedAmount: amount));
        }
    }

    private sealed class FakeCatalogRepository : ILocalCatalogRepository
    {
        public IReadOnlyList<SellableItemDto> Items { get; init; } = [];

        public IReadOnlyList<SellableItemDto> SpecialItems { get; init; } = [];

        public Exception? LoadSellableItemsException { get; init; }

        public int LoadSellableItemsCallCount { get; private set; }

        public int LoadSpecialProductItemsCallCount { get; private set; }

        public Func<Task>? BeforeLoadSellableItemsAsync { get; init; }

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
            var rows = Items
                .Where(item => string.Equals(item.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
                .Select(item => new LocalSellableItemCompareRow(
                    item.StoreCode,
                    item.LookupCode,
                    item.ProductCode,
                    item.UpdatedAt))
                .OrderBy(row => row.LookupCodeNormalized, StringComparer.Ordinal)
                .Where(row => string.IsNullOrWhiteSpace(afterLookupCodeNormalized)
                    || string.Compare(row.LookupCodeNormalized, afterLookupCodeNormalized, StringComparison.Ordinal) > 0)
                .Take(pageSize)
                .ToArray();
            return Task.FromResult<IReadOnlyList<LocalSellableItemCompareRow>>(rows);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default)
        {
            LoadSellableItemsCallCount++;
            if (BeforeLoadSellableItemsAsync is not null)
            {
                return LoadSellableItemsCoreAsync(Items);
            }

            return LoadSellableItemsException is null
                ? Task.FromResult(Items)
                : Task.FromException<IReadOnlyList<SellableItemDto>>(LoadSellableItemsException);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            LoadSellableItemsCallCount++;
            var storeItems = Items
                .Where(item => string.Equals(item.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (BeforeLoadSellableItemsAsync is not null)
            {
                return LoadSellableItemsCoreAsync(storeItems);
            }

            return LoadSellableItemsException is null
                ? Task.FromResult<IReadOnlyList<SellableItemDto>>(storeItems)
                : Task.FromException<IReadOnlyList<SellableItemDto>>(LoadSellableItemsException);
        }

        private async Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsCoreAsync(IReadOnlyList<SellableItemDto> items)
        {
            await BeforeLoadSellableItemsAsync!();
            if (LoadSellableItemsException is not null)
            {
                throw LoadSellableItemsException;
            }

            return items;
        }
    }

    private sealed class FakeCatalogSyncService : ILocalCatalogSyncService
    {
        public int FullSyncCallCount { get; private set; }

        public Exception? FullSyncException { get; init; }

        public Task<LocalCatalogSyncResult> FullSyncAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<CatalogSyncProgress>? progress = null,
            bool forceFullDownload = false)
        {
            FullSyncCallCount++;
            return FullSyncException is null
                ? Task.FromResult(new LocalCatalogSyncResult(storeCode, 0, 0, 0, 0))
                : Task.FromException<LocalCatalogSyncResult>(FullSyncException);
        }
    }

    private sealed class RecordingShellCatalogService : IShellCatalogService
    {
        public IReadOnlyList<SellableItemDto> LocalItems { get; init; } = [];

        public IReadOnlyList<SellableItemDto> SyncItems { get; init; } = [];

        public Exception? SyncException { get; init; }

        public int SyncCallCount { get; private set; }

        public CancellationToken LastSyncCancellationToken { get; private set; }

        public bool IsCatalogSyncActive => false;

        public Task ReplacePreviewCatalogAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadLocalCatalogAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LocalItems);
        }

        public Task<IReadOnlyList<SellableItemDto>> SyncCatalogAndReloadAsync(
            string storeCode,
            bool forceFullDownload,
            IProgress<CatalogSyncProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            SyncCallCount++;
            LastSyncCancellationToken = cancellationToken;
            return SyncException is null
                ? Task.FromResult(SyncItems)
                : Task.FromException<IReadOnlyList<SellableItemDto>>(SyncException);
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

    private sealed class FakeConnectivityApiClient(params bool[] responses) : IConnectivityApiClient
    {
        private readonly Queue<bool> _responses = new(responses);

        public Exception? CheckOnlineException { get; init; }

        public int CheckOnlineCallCount { get; private set; }

        public Task<bool> CheckOnlineAsync(CancellationToken cancellationToken = default)
        {
            CheckOnlineCallCount++;
            if (CheckOnlineException is not null)
            {
                return Task.FromException<bool>(CheckOnlineException);
            }

            return Task.FromResult(_responses.Count > 0 && _responses.Dequeue());
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

        public TaskCompletionSource<IReadOnlyList<StoreSelectionItem>>? PendingStoresResult { get; init; }

        public Exception? GetStoresException { get; init; }

        public DeviceReregisterResponse? ReregisterResponse { get; init; }

        public TaskCompletionSource<DeviceReregisterResponse>? PendingReregisterResponse { get; init; }

        private TaskCompletionSource ReregisterStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DeviceReregisterRequest? LastReregisterRequest { get; private set; }

        public Task<IReadOnlyList<StoreSelectionItem>> GetStoresAsync(CancellationToken cancellationToken = default)
        {
            GetStoresCallCount++;
            if (PendingStoresResult is not null)
            {
                return PendingStoresResult.Task;
            }

            return GetStoresException is null
                ? Task.FromResult(Stores)
                : Task.FromException<IReadOnlyList<StoreSelectionItem>>(GetStoresException);
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
            ReregisterStarted.TrySetResult();
            return PendingReregisterResponse?.Task
                ?? Task.FromResult(ReregisterResponse ?? new DeviceReregisterResponse("POS-NEW", request.TargetStoreCode, "New Store", -1, false, "Pending approval"));
        }

        public Task WaitForReregisterStartedAsync() => ReregisterStarted.Task;
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

    private sealed class RecordingReceiptPrintService : IReceiptPrintService
    {
        public List<ReceiptPrintCall> Calls { get; } = [];

        public Task<ReceiptPrintResult> PrintLatestReceiptAsync(
            ReceiptPrintReason reason = ReceiptPrintReason.LastReceipt,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new ReceiptPrintCall(null, reason));
            return Task.FromResult(new ReceiptPrintResult(true, "printed"));
        }

        public Task<ReceiptPrintResult> PrintReceiptAsync(
            Guid orderGuid,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new ReceiptPrintCall(orderGuid, reason));
            return Task.FromResult(new ReceiptPrintResult(true, "printed", orderGuid));
        }

        public Task<ReceiptPrintResult> PrintReceiptAsync(
            ReceiptDetails receipt,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new ReceiptPrintCall(receipt.OrderGuid, reason));
            return Task.FromResult(new ReceiptPrintResult(true, "printed", receipt.OrderGuid));
        }

        public Task<ReceiptPrintResult> TestPrinterAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add(new ReceiptPrintCall(null, ReceiptPrintReason.Test));
            return Task.FromResult(new ReceiptPrintResult(true, "tested"));
        }
    }

    private sealed class RecordingCashDrawerService : ICashDrawerService
    {
        public int OpenCallCount { get; private set; }

        public ReceiptPrintResult Result { get; init; } = new(true, "Cash drawer opened.");

        public Task<ReceiptPrintResult> OpenAsync(CancellationToken cancellationToken = default)
        {
            OpenCallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingApplicationExitService : IApplicationExitService
    {
        public int ExitCallCount { get; private set; }

        public void Exit()
        {
            ExitCallCount++;
        }
    }

    private sealed class FakeConfirmationDialogService : IConfirmationDialogService
    {
        public bool ConfirmExitApplicationResult { get; init; }

        public int ConfirmExitApplicationCallCount { get; private set; }

        public bool ConfirmExitApplication()
        {
            ConfirmExitApplicationCallCount++;
            return ConfirmExitApplicationResult;
        }

        public bool ConfirmResetTestSalesData()
        {
            return false;
        }
    }

    private sealed record ReceiptPrintCall(Guid? OrderGuid, ReceiptPrintReason Reason);

    private sealed class FakeSyncQueueRepository : ISyncQueueRepository
    {
        public SyncQueueOverview Overview { get; set; } = new(0, 0, 0, null);

        public IReadOnlyList<SyncQueueListItem> ActiveItems { get; set; } = [];

        public bool ThrowOnRead { get; set; }

        public int ThrowOnReadAfterCount { get; set; } = -1;

        private int _readCount;

        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured();
            return Task.FromResult(Overview.PendingCount);
        }

        public Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured();
            return Task.FromResult(Overview);
        }

        public Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured();
            return Task.FromResult(ActiveItems);
        }

        private void ThrowIfConfigured()
        {
            _readCount++;
            if (ThrowOnRead || (ThrowOnReadAfterCount >= 0 && _readCount > ThrowOnReadAfterCount))
            {
                throw new InvalidOperationException("sync queue read failed");
            }
        }
    }

    private sealed class FakeOrderUploadExecutionService : IOrderUploadExecutionService
    {
        public OrderUploadExecutionResult ExecuteOneResult { get; init; } = new(1, 1, 0);

        public OrderUploadExecutionResult ExecutePendingResult { get; init; } = new(1, 1, 0);

        public Exception? ExecutePendingException { get; init; }

        public TaskCompletionSource? PendingExecutionStarted { get; init; }

        public TaskCompletionSource? ReleasePendingExecution { get; init; }

        public Guid? LastExecuteOneOrderGuid { get; private set; }

        public int ExecutePendingCallCount { get; private set; }

        public Action<Guid>? OnExecuteOne { get; init; }

        public Action? OnExecutePending { get; init; }

        public Action? OnBeforeExecutePendingException { get; init; }

        public Task<OrderUploadExecutionResult> ExecuteOneAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            LastExecuteOneOrderGuid = orderGuid;
            OnExecuteOne?.Invoke(orderGuid);
            return Task.FromResult(ExecuteOneResult);
        }

        public async Task<OrderUploadExecutionResult> ExecutePendingAsync(int batchSize = 20, CancellationToken cancellationToken = default)
        {
            ExecutePendingCallCount++;
            PendingExecutionStarted?.TrySetResult();
            if (ReleasePendingExecution is not null)
            {
                await ReleasePendingExecution.Task;
            }

            if (ExecutePendingException is not null)
            {
                OnBeforeExecutePendingException?.Invoke();
                throw ExecutePendingException;
            }

            OnExecutePending?.Invoke();
            return ExecutePendingResult;
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

        public int PrewarmCallCount { get; private set; }

        public int ToggleCallCount { get; private set; }

        public int SetModeCallCount { get; private set; }

        public int WindowCreationCount { get; private set; }

        public CustomerDisplayWindowMode LastSetMode { get; private set; } = CustomerDisplayWindowMode.Closed;

        public event EventHandler? Closed;

        public void Prewarm(CustomerDisplayViewModel viewModel)
        {
            PrewarmCallCount++;
            EnsureWindowCreated();
        }

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
            if (result.Mode == CustomerDisplayWindowMode.Closed)
            {
                _hasWindow = false;
            }
            else
            {
                EnsureWindowCreated();
            }

            Mode = result.Mode;
            return result;
        }

        public void RaiseClosed()
        {
            _hasWindow = false;
            Mode = CustomerDisplayWindowMode.Closed;
            Closed?.Invoke(this, EventArgs.Empty);
        }

        private bool _hasWindow;

        private void EnsureWindowCreated()
        {
            if (_hasWindow)
            {
                return;
            }

            _hasWindow = true;
            WindowCreationCount++;
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

    private sealed class FakeSpecialProductsWorkflowService : ISpecialProductsWorkflowService
    {
        public SpecialProductsLoadResult PreloadResult { get; init; } = new("1042", []);

        public Exception? PreloadException { get; init; }

        public int PreloadCallCount { get; private set; }

        public string? LastPreloadStoreCode { get; private set; }

        public Task<SpecialProductsLoadResult> PreloadAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            PreloadCallCount++;
            LastPreloadStoreCode = storeCode;
            return PreloadException is null
                ? Task.FromResult(PreloadResult)
                : Task.FromException<SpecialProductsLoadResult>(PreloadException);
        }

        public Task<SpecialProductsLoadResult> EnsureLoadedAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PreloadResult with { StoreCode = storeCode });
        }

        public Task<SpecialProductsLoadResult> LoadAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PreloadResult with { StoreCode = storeCode });
        }

        public SpecialProductsSearchResult Search(string storeCode, string searchText)
        {
            return new SpecialProductsSearchResult(storeCode, searchText, []);
        }

        public SpecialProductsAddToCartResult AddToCart(SellableItemDto item)
        {
            return new SpecialProductsAddToCartResult(new CartLine(item), 1);
        }

        public Task<SpecialProductsDownloadWorkflowResult> DownloadAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<SpecialProductDownloadProgress>? progress = null)
        {
            return Task.FromResult(new SpecialProductsDownloadWorkflowResult(
                new SpecialProductDownloadResult(storeCode, 0, 0, 0, 0, 0),
                []));
        }

        public Task<SpecialProductsMutationWorkflowResult> MarkSpecialProductAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SpecialProductsMutationWorkflowResult(
                storeCode,
                productCode,
                isSpecialProduct,
                []));
        }

        public Task<SpecialProductsReorderWorkflowResult?> ReorderAsync(
            string storeCode,
            IReadOnlyList<SellableItemDto> currentItems,
            string productCode,
            int delta,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SpecialProductsReorderWorkflowResult?>(null);
        }
    }

    private sealed class FakeCardPaymentRecoveryService : ICardPaymentRecoveryService
    {
        private readonly Queue<Func<PosCartService, PosSessionState, CancellationToken, Task<CardPaymentRecoveryResult>>> _results;

        public FakeCardPaymentRecoveryService(params Task<CardPaymentRecoveryResult>[] results)
        {
            _results = new Queue<Func<PosCartService, PosSessionState, CancellationToken, Task<CardPaymentRecoveryResult>>>(
                results.Select(result => new Func<PosCartService, PosSessionState, CancellationToken, Task<CardPaymentRecoveryResult>>(
                    (PosCartService cart, PosSessionState session, CancellationToken cancellationToken) => result)));
        }

        public FakeCardPaymentRecoveryService(params Func<PosCartService, PosSessionState, CancellationToken, Task<CardPaymentRecoveryResult>>[] results)
        {
            _results = new Queue<Func<PosCartService, PosSessionState, CancellationToken, Task<CardPaymentRecoveryResult>>>(results);
        }

        public int CallCount { get; private set; }

        public Task<CardPaymentRecoveryResult> RecoverLatestAsync(
            PosCartService cart,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _results.Count > 0
                ? _results.Dequeue()(cart, session, cancellationToken)
                : Task.FromResult(CardPaymentRecoveryResult.None);
        }

        public Task<CardPaymentRecoveryResult> RecoverActiveSessionAsync(
            PosCartService cart,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _results.Count > 0
                ? _results.Dequeue()(cart, session, cancellationToken)
                : Task.FromResult(CardPaymentRecoveryResult.None);
        }
    }

    private sealed class ConsoleLogCapture : IDisposable
    {
        private readonly List<string> _lines = [];

        public ConsoleLogCapture()
        {
            ConsoleLog.LineWritten += OnLineWritten;
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        public void Dispose()
        {
            ConsoleLog.LineWritten -= OnLineWritten;
        }

        private void OnLineWritten(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }
}
