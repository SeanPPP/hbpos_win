using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const string DefaultTestStoreCode = "1002";

    private readonly LocalSellableItemIndex _priceIndex;
    private readonly PosCartService _cart;
    private readonly CashCheckoutService _checkout;
    private readonly ILocalSchemaService _schema;
    private readonly ILocalCatalogRepository _catalogRepository;
    private readonly IShellCultureService _shellCultureService;
    private readonly IShellCatalogService _shellCatalogService;
    private readonly IRemoteLookupRefreshService _remoteLookupRefresh;
    private readonly ISpecialProductService _specialProductService;
    private readonly IConnectivityApiClient _connectivityApiClient;
    private readonly IMainShellStartupService _mainShellStartupService;
    private readonly ILocalOrderRepository _orderRepository;
    private readonly IShellSyncCenterService _shellSyncCenterService;
    private readonly ILocalizationService _localization;
    private readonly ICustomerDisplayOrchestrator _customerDisplayOrchestrator;
    private readonly IRawScannerService _rawScannerService;
    private readonly IReceiptQueryService _receiptQueryService;
    private readonly ISuspendedOrderService? _suspendedOrderService;
    private readonly IRemoteOrderHistoryService? _remoteOrderHistoryService;
    private readonly ICashPaymentWorkflowService _cashPaymentWorkflowService;
    private readonly IDeviceRegistrationWorkflowService _deviceRegistrationWorkflowService;
    private readonly ISpecialProductsWorkflowService _specialProductsWorkflowService;
    private readonly PosTerminalWorkflowFactory _posTerminalWorkflowFactory;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _connectivityTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _catalogDownloadHideTimer = new();

    private bool _isApplyingCulture;
    private bool _isRefreshingConnectivity;
    private bool _schemaReady;
    private LocalOrder? _lastCompletedOrder;
    private LocalDeviceCache? _pendingDeviceRegistrationCache;
    private Task? _deviceRegistrationStoreLoadTask;
    private Task? _posPostShowStartupTask;
    private AppStartupOptions? _startupOptions;

    [ObservableProperty]
    private PosSessionState _session = new("HB POS", DefaultTestStoreCode, "Main Branch", "Terminal 04", "C001", "Alice", false, 0);

    [ObservableProperty]
    private object? _currentScreen;

    [ObservableProperty]
    private string _selectedCultureName = LocalizationService.DefaultCultureName;

    [ObservableProperty]
    private string _onlineStateText = string.Empty;

    [ObservableProperty]
    private string _pendingSyncText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _currentTime = string.Empty;

    [ObservableProperty]
    private string _terminalInfo = string.Empty;

    [ObservableProperty]
    private string _storeInfo = string.Empty;

    [ObservableProperty]
    private string _cashierInfo = string.Empty;

    [ObservableProperty]
    private string _versionStatusText = string.Empty;

    [ObservableProperty]
    private string _orderSyncStatusText = string.Empty;

    [ObservableProperty]
    private string _syncCenterDetailTitle = string.Empty;

    [ObservableProperty]
    private string _lastOrderSyncErrorText = string.Empty;

    [ObservableProperty]
    private bool _isSyncCenterExpanded;

    [ObservableProperty]
    private bool _isCustomerDisplayOpen;

    [ObservableProperty]
    private CustomerDisplayWindowMode _customerDisplayWindowMode = CustomerDisplayWindowMode.Closed;

    [ObservableProperty]
    private int _pendingUploadCount;

    [ObservableProperty]
    private int _failedUploadCount;

    [ObservableProperty]
    private int _syncingOrderCount;

    [ObservableProperty]
    private bool _isCatalogDownloadProgressVisible;

    [ObservableProperty]
    private double _catalogDownloadProgressValue;

    [ObservableProperty]
    private string _catalogDownloadProgressText = string.Empty;

    [ObservableProperty]
    private string _catalogDownloadProgressDetailText = string.Empty;

    [ObservableProperty]
    private bool _isCatalogDownloadProgressFailed;

    [ActivatorUtilitiesConstructor]
    public MainViewModel(
        LocalSellableItemIndex priceIndex,
        PosCartService cart,
        CashCheckoutService checkout,
        ILocalSchemaService schema,
        ILocalAppSettingsRepository settingsRepository,
        ILocalCatalogRepository catalogRepository,
        ILocalCatalogSyncService catalogSync,
        IRemoteLookupRefreshService remoteLookupRefresh,
        ISpecialProductService specialProductService,
        IConnectivityApiClient connectivityApiClient,
        ILocalDeviceRepository deviceRepository,
        IDeviceApiClient deviceApiClient,
        IDeviceFingerprintService fingerprintService,
        DeviceAuthorizationState deviceAuthorizationState,
        ILocalOrderRepository orderRepository,
        ISyncQueueRepository syncQueueRepository,
        ILocalizationService localization,
        ICustomerDisplayWindowService customerDisplayWindowService,
        IRawScannerService rawScannerService)
        : this(
            priceIndex,
            cart,
            checkout,
            schema,
            new ShellCultureService(localization, settingsRepository),
            new ShellCatalogService(priceIndex, catalogRepository, catalogSync),
            catalogRepository,
            remoteLookupRefresh,
            specialProductService,
            connectivityApiClient,
            new MainShellStartupService(deviceRepository, fingerprintService, deviceAuthorizationState),
            orderRepository,
            new ShellSyncCenterService(syncQueueRepository),
            localization,
            new CustomerDisplayOrchestrator(customerDisplayWindowService),
            rawScannerService,
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueueRepository),
            new DeviceRegistrationWorkflowService(deviceApiClient, deviceRepository, fingerprintService),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepository, specialProductService),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync))
    {
    }

    public MainViewModel(
        LocalSellableItemIndex priceIndex,
        PosCartService cart,
        CashCheckoutService checkout,
        ILocalSchemaService schema,
        IShellCultureService shellCultureService,
        IShellCatalogService shellCatalogService,
        ILocalCatalogRepository catalogRepository,
        IRemoteLookupRefreshService remoteLookupRefresh,
        ISpecialProductService specialProductService,
        IConnectivityApiClient connectivityApiClient,
        IMainShellStartupService mainShellStartupService,
        ILocalOrderRepository orderRepository,
        IShellSyncCenterService shellSyncCenterService,
        ILocalizationService localization,
        ICustomerDisplayOrchestrator customerDisplayOrchestrator,
        IRawScannerService rawScannerService,
        IReceiptQueryService receiptQueryService,
        ICashPaymentWorkflowService cashPaymentWorkflowService,
        IDeviceRegistrationWorkflowService deviceRegistrationWorkflowService,
        ISpecialProductsWorkflowService specialProductsWorkflowService,
        PosTerminalWorkflowFactory posTerminalWorkflowFactory,
        ISuspendedOrderService? suspendedOrderService = null,
        IRemoteOrderHistoryService? remoteOrderHistoryService = null)
    {
        _priceIndex = priceIndex;
        _cart = cart;
        _checkout = checkout;
        _schema = schema;
        _shellCultureService = shellCultureService;
        _shellCatalogService = shellCatalogService;
        _catalogRepository = catalogRepository;
        _remoteLookupRefresh = remoteLookupRefresh;
        _specialProductService = specialProductService;
        _connectivityApiClient = connectivityApiClient;
        _mainShellStartupService = mainShellStartupService;
        _orderRepository = orderRepository;
        _shellSyncCenterService = shellSyncCenterService;
        _localization = localization;
        _customerDisplayOrchestrator = customerDisplayOrchestrator;
        _rawScannerService = rawScannerService;
        _receiptQueryService = receiptQueryService;
        _suspendedOrderService = suspendedOrderService;
        _remoteOrderHistoryService = remoteOrderHistoryService;
        _cashPaymentWorkflowService = cashPaymentWorkflowService;
        _deviceRegistrationWorkflowService = deviceRegistrationWorkflowService;
        _specialProductsWorkflowService = specialProductsWorkflowService;
        _posTerminalWorkflowFactory = posTerminalWorkflowFactory;

        PaymentSuccess = new PaymentSuccessViewModel(_receiptQueryService);

        ShowPosCommand = new RelayCommand(ShowPos);
        ShowCashPaymentCommand = new RelayCommand(ShowCashPayment, () => !_cart.IsEmpty);
        ShowPaymentSuccessCommand = new AsyncRelayCommand(ShowPaymentSuccessLatestAsync);
        ShowHistoryCommand = new AsyncRelayCommand(ShowHistoryAsync);
        ShowCustomerDisplayCommand = new RelayCommand(ShowCustomerDisplay);
        ToggleSyncCenterCommand = new AsyncRelayCommand(ToggleSyncCenterAsync);
        ToggleCustomerDisplayWindowCommand = new RelayCommand(ToggleCustomerDisplayWindow);
        CloseCustomerDisplayWindowCommand = new RelayCommand(CloseCustomerDisplayWindow);
        ShowCustomerDisplayNormalCommand = new RelayCommand(ShowCustomerDisplayNormal);
        ShowCustomerDisplayFullscreenCommand = new RelayCommand(ShowCustomerDisplayFullscreen);
        ToggleCultureCommand = new AsyncRelayCommand(ToggleCultureAsync);
        ResetScannerBindingCommand = new AsyncRelayCommand(ResetScannerBindingAsync);

        _cart.CartChanged += OnCartChanged;
        _localization.CultureChanged += OnCultureChanged;
        _customerDisplayOrchestrator.Closed += (_, _) => CustomerDisplayWindowMode = CustomerDisplayWindowMode.Closed;
        _clockTimer.Tick += (_, _) => RefreshClock();
        _connectivityTimer.Tick += async (_, _) => await RefreshOnlineStateAsync(CancellationToken.None);
        _catalogDownloadHideTimer.Tick += (_, _) =>
        {
            _catalogDownloadHideTimer.Stop();
            IsCatalogDownloadProgressVisible = false;
        };
        RefreshLocalizedShell(resetStatus: true);
    }

    public PosTerminalViewModel? PosTerminal { get; private set; }

    public SpecialProductsViewModel? SpecialProducts { get; private set; }

    public CashPaymentViewModel? CashPayment { get; private set; }

    public PaymentSuccessViewModel PaymentSuccess { get; }

    public TransactionHistoryViewModel? TransactionHistory { get; private set; }

    public CustomerDisplayViewModel CustomerDisplay { get; } = new();

    public DeviceRegistrationViewModel? DeviceRegistration { get; private set; }

    public ObservableCollection<SyncQueueListItem> SyncCenterOrders { get; } = [];

    public IRelayCommand ShowPosCommand { get; }

    public IRelayCommand ShowCashPaymentCommand { get; }

    public IAsyncRelayCommand ShowPaymentSuccessCommand { get; }

    public IAsyncRelayCommand ShowHistoryCommand { get; }

    public IRelayCommand ShowCustomerDisplayCommand { get; }

    public IAsyncRelayCommand ToggleSyncCenterCommand { get; }

    public IRelayCommand ToggleCustomerDisplayWindowCommand { get; }

    public IRelayCommand CloseCustomerDisplayWindowCommand { get; }

    public IRelayCommand ShowCustomerDisplayNormalCommand { get; }

    public IRelayCommand ShowCustomerDisplayFullscreenCommand { get; }

    public IAsyncRelayCommand ToggleCultureCommand { get; }

    public IAsyncRelayCommand ResetScannerBindingCommand { get; }

    public async Task InitializeAsync(AppStartupOptions startupOptions)
    {
        _startupOptions = startupOptions;
        await _schema.InitializeAsync();
        _schemaReady = true;

        await RestoreLanguageAsync(startupOptions);
        var startupResult = await _mainShellStartupService.EvaluateAsync(Session, startupOptions.PreviewMode);
        Session = startupResult.Session;
        if (startupResult.RequiresDeviceRegistration)
        {
            DeviceRegistration = CreateDeviceRegistrationViewModel(startupOptions);
            _pendingDeviceRegistrationCache = startupResult.CachedDevice;
            DeviceRegistration.Prepare(startupResult.CachedDevice);
            CurrentScreen = DeviceRegistration;
            RefreshClock();
            _clockTimer.Start();
            return;
        }

        await InitializePosExperienceAsync(startupOptions);
    }

    public Task ContinueStartupAfterShownAsync(AppStartupOptions startupOptions, Window? owner = null)
    {
        if (startupOptions.PreviewMode)
        {
            return Task.CompletedTask;
        }

        if (DeviceRegistration is not null && CurrentScreen == DeviceRegistration)
        {
            _deviceRegistrationStoreLoadTask ??= DeviceRegistration.LoadStoresAsync(_pendingDeviceRegistrationCache);
            return _deviceRegistrationStoreLoadTask;
        }

        return ContinuePosStartupAfterShownAsync(startupOptions, owner);
    }

    public bool TryProcessKeyboardScannerInput(string barcode)
    {
        if (CurrentScreen == PosTerminal && PosTerminal is not null)
        {
            PosTerminal.ProcessScannerBarcode(barcode, "keyboard-focus-fallback", "keyboard-fallback");
            return true;
        }

        if (CurrentScreen == SpecialProducts && SpecialProducts is not null)
        {
            SpecialProducts.ProcessScannerBarcode(barcode, "keyboard-focus-fallback", "keyboard-fallback");
            return true;
        }

        ConsoleLog.Write(
            "RawScanner",
            $"keyboard fallback scan ignored because active screen cannot handle scanner screen={CurrentScreen?.GetType().Name ?? "<none>"} barcode={barcode}");
        return false;
    }

    private async Task ActivateDeviceAsync(DeviceActivatedEventArgs args, AppStartupOptions startupOptions)
    {
        _deviceRegistrationStoreLoadTask = null;
        _posPostShowStartupTask = null;
        Session = Session with
        {
            StoreCode = args.StoreCode,
            StoreName = args.StoreName,
            DeviceCode = args.DeviceCode
        };
        if (!string.IsNullOrWhiteSpace(args.AuthorizationCode))
        {
            _mainShellStartupService.SetAuthorizedDevice(
                args.DeviceCode,
                args.StoreCode,
                args.HardwareId,
                args.AuthorizationCode);
        }

        await InitializePosExperienceAsync(startupOptions);
        _ = ContinuePosStartupAfterShownAsync(startupOptions, Application.Current.MainWindow);
    }

    private async Task InitializePosExperienceAsync(AppStartupOptions startupOptions)
    {
        PosTerminal?.Dispose();
        SpecialProducts?.Dispose();
        IReadOnlyList<SellableItemDto> cachedItems = [];
        if (startupOptions.PreviewMode)
        {
            cachedItems = CreateStarterItems();
            await _shellCatalogService.ReplacePreviewCatalogAsync(cachedItems);
        }
        else
        {
            cachedItems = await LoadStartupCatalogIndexAsync();
        }

        var posWorkflowService = _posTerminalWorkflowFactory(RefreshRemoteLookupAsync, ReloadCatalogIndexAsync);
        PosTerminal = new PosTerminalViewModel(
            _priceIndex,
            _cart,
            Session,
            ShowCashPayment,
            ShowSpecialProductsAsync,
            _localization,
            onHoldOrderAsync: SuspendCurrentOrderAsync,
            onRecallOrderAsync: ShowSuspendedHistoryAsync,
            syncCatalogAsync: SyncCatalogAndReloadAsync,
            resetCatalogAsync: ResetCatalogAndReloadAsync,
            refreshOnlineAsync: RefreshOnlineStateAsync,
            rawScannerService: _rawScannerService,
            onReregisterDeviceAsync: BeginDeviceReregistrationAsync,
            workflowService: posWorkflowService);
        SpecialProducts = new SpecialProductsViewModel(
            _priceIndex,
            _cart,
            _catalogRepository,
            _specialProductService,
            Session,
            _localization,
            ShowPos,
            line => PosTerminal?.RevealCartLine(line),
            _specialProductsWorkflowService,
            _rawScannerService);
        if (cachedItems.Count > 0)
        {
            PosTerminal.LoadMatches(cachedItems);
        }

        TransactionHistory = CreateTransactionHistoryViewModel();
        PaymentSuccess.NewTransactionRequested += (_, _) => ResetForNewTransaction();

        if (startupOptions.PreviewMode)
        {
            AddPreviewCartItems(cachedItems);
            _lastCompletedOrder = await CreatePreviewOrderAsync(cachedItems);
        }

        await RefreshPendingSyncAsync();
        RefreshClock();
        _clockTimer.Start();
        ApplySessionToScreens();
        NavigateFromStartup(startupOptions.InitialScreen);
        BeginSpecialProductsPreload();

    }

    private Task ContinuePosStartupAfterShownAsync(AppStartupOptions startupOptions, Window? owner)
    {
        if (startupOptions.PreviewMode)
        {
            return Task.CompletedTask;
        }

        _posPostShowStartupTask ??= ContinuePosStartupAfterShownCoreAsync(owner);
        return _posPostShowStartupTask;
    }

    private async Task ContinuePosStartupAfterShownCoreAsync(Window? owner)
    {
        OpenCustomerDisplayWindow(owner);

        await RefreshOnlineStateAsync(CancellationToken.None);
        _connectivityTimer.Start();
        BeginInitialCatalogSync();
    }

    private async Task<IReadOnlyList<SellableItemDto>> LoadStartupCatalogIndexAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write("CatalogStartup", $"local catalog load start store={Session.StoreCode}");
        try
        {
            var cachedItems = await _shellCatalogService.LoadLocalCatalogAsync(Session.StoreCode, CancellationToken.None);
            PosTerminal?.LoadMatches(cachedItems);
            PosTerminal?.RefreshCart();
            CashPayment?.RefreshCart();
            stopwatch.Stop();
            ConsoleLog.Write("CatalogStartup", $"local catalog load completed store={Session.StoreCode} items={cachedItems.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return cachedItems;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write("CatalogStartup", $"local catalog load failed store={Session.StoreCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            StatusMessage = ex.Message;
            return [];
        }
    }

    partial void OnSessionChanged(PosSessionState value)
    {
        RefreshLocalizedShell();
        ApplySessionToScreens();
    }

    partial void OnCurrentScreenChanged(object? value)
    {
        var activePageId = value == PosTerminal
            ? PosTerminalViewModel.PageId
            : value == SpecialProducts
                ? SpecialProductsViewModel.PageId
                : null;
        _rawScannerService.SetActivePage(activePageId);
    }

    partial void OnCustomerDisplayWindowModeChanged(CustomerDisplayWindowMode value)
    {
        IsCustomerDisplayOpen = value != CustomerDisplayWindowMode.Closed;
    }

    partial void OnSelectedCultureNameChanged(string value)
    {
        if (_isApplyingCulture || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _ = ApplyLanguageAsync(value, persist: true);
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedShell();
        OnPropertyChanged(nameof(SelectedCultureName));
    }

    private async Task RestoreLanguageAsync(AppStartupOptions startupOptions)
    {
        ApplySelectedCultureName(await _shellCultureService.RestoreAsync(startupOptions, _schemaReady));
    }

    private async Task ApplyLanguageAsync(string cultureName, bool persist)
    {
        ApplySelectedCultureName(await _shellCultureService.ApplyAsync(cultureName, persist, _schemaReady));
    }

    private async Task ToggleCultureAsync()
    {
        ApplySelectedCultureName(await _shellCultureService.ToggleAsync(_schemaReady));
    }

    private void RefreshLocalizedShell(bool resetStatus = false)
    {
        OnlineStateText = _localization.T(Session.IsOnline ? "Online" : "Offline");
        PendingSyncText = string.Format(_localization.CurrentCulture, _localization.T("pos.status.pendingSync"), Session.PendingSyncCount);
        TerminalInfo = string.Format(_localization.CurrentCulture, _localization.T("shell.footer.terminalInfo"), Session.DeviceCode);
        StoreInfo = string.Format(_localization.CurrentCulture, _localization.T("shell.top.storeInfo"), Session.StoreName, Session.StoreCode);
        CashierInfo = string.Format(_localization.CurrentCulture, _localization.T("shell.top.cashierInfo"), Session.CashierName);
        VersionStatusText = _localization.T("shell.footer.versionReady");
        OrderSyncStatusText = string.Format(
            _localization.CurrentCulture,
            _localization.T("shell.sync.orderStatus"),
            PendingUploadCount,
            FailedUploadCount,
            SyncingOrderCount);
        SyncCenterDetailTitle = string.Format(
            _localization.CurrentCulture,
            _localization.T("shell.sync.detailTitle"),
            SyncCenterOrders.Count);
        if (resetStatus || string.IsNullOrWhiteSpace(StatusMessage))
        {
            StatusMessage = _localization.T("StatusOfflineReady");
        }
    }

    private async Task RefreshPendingSyncAsync()
    {
        ApplySyncCenterSnapshot(await _shellSyncCenterService.GetSnapshotAsync());
    }

    private DeviceRegistrationViewModel CreateDeviceRegistrationViewModel(AppStartupOptions startupOptions)
    {
        var viewModel = new DeviceRegistrationViewModel(_deviceRegistrationWorkflowService);
        viewModel.DeviceActivated += async (_, args) => await ActivateDeviceAsync(args, startupOptions);
        viewModel.DeviceReregistered += (_, _) => ApplyDeviceReregistered();
        viewModel.CancelRequested += (_, _) => CancelDeviceReregistration();
        return viewModel;
    }

    private void ApplySelectedCultureName(string cultureName)
    {
        _isApplyingCulture = true;
        SelectedCultureName = cultureName;
        _isApplyingCulture = false;
    }

    private void ApplySyncCenterSnapshot(ShellSyncCenterSnapshot snapshot)
    {
        PendingUploadCount = snapshot.Overview.PendingCount;
        FailedUploadCount = snapshot.Overview.FailedCount;
        SyncingOrderCount = snapshot.Overview.SyncingCount;
        LastOrderSyncErrorText = snapshot.Overview.LastError ?? _localization.T("shell.sync.noErrors");
        SyncCenterOrders.ReplaceWith(snapshot.ActiveItems);
        Session = Session with { PendingSyncCount = snapshot.Overview.PendingCount };
        RefreshLocalizedShell();
    }

    private void RefreshClock()
    {
        CurrentTime = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private void ApplySessionToScreens()
    {
        if (PosTerminal is not null)
        {
            PosTerminal.Session = Session;
        }

        if (CashPayment is not null)
        {
            CashPayment.Session = Session;
        }

        if (SpecialProducts is not null)
        {
            SpecialProducts.Session = Session;
        }

        if (TransactionHistory is not null)
        {
            TransactionHistory.Session = Session;
        }
    }

    private void BeginInitialCatalogSync()
    {
        if (!Session.IsOnline)
        {
            return;
        }

        _ = TryInitialCatalogSyncAsync();
    }

    private void BeginSpecialProductsPreload()
    {
        if (SpecialProducts is null)
        {
            return;
        }

        _ = TryPreloadSpecialProductsAsync();
    }

    private async Task TryPreloadSpecialProductsAsync()
    {
        try
        {
            if (SpecialProducts is not null)
            {
                await SpecialProducts.PreloadAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("SpecialProducts", $"startup preload failed store={Session.StoreCode} error={ex.Message}");
        }
    }

    private async Task<bool> RefreshOnlineStateAsync(CancellationToken cancellationToken)
    {
        if (_isRefreshingConnectivity)
        {
            return Session.IsOnline;
        }

        _isRefreshingConnectivity = true;
        try
        {
            var isOnline = await _connectivityApiClient.CheckOnlineAsync(cancellationToken);
            if (Session.IsOnline != isOnline)
            {
                Session = Session with { IsOnline = isOnline };
            }

            return isOnline;
        }
        finally
        {
            _isRefreshingConnectivity = false;
        }
    }

    private async Task TryInitialCatalogSyncAsync()
    {
        try
        {
            var cachedItems = await SyncCatalogAndReloadAsync(CancellationToken.None);
            PosTerminal?.LoadMatches(cachedItems);
            PosTerminal?.RefreshCart();
            CashPayment?.RefreshCart();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"Catalog sync failed: {ex.Message}";
        }
    }

    private Task<RemoteLookupRefreshResult> RefreshRemoteLookupAsync(
        string storeCode,
        string lookupCode,
        CancellationToken cancellationToken)
    {
        return _remoteLookupRefresh.RefreshLookupAsync(storeCode, lookupCode, cancellationToken);
    }

    private Task<IReadOnlyList<SellableItemDto>> SyncCatalogAndReloadAsync(CancellationToken cancellationToken)
    {
        return SyncCatalogAndReloadAsync(forceFullDownload: false, cancellationToken);
    }

    private Task<IReadOnlyList<SellableItemDto>> ResetCatalogAndReloadAsync(CancellationToken cancellationToken)
    {
        return SyncCatalogAndReloadAsync(forceFullDownload: true, cancellationToken);
    }

    private async Task<IReadOnlyList<SellableItemDto>> SyncCatalogAndReloadAsync(
        bool forceFullDownload,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<CatalogSyncProgress>(ApplyCatalogDownloadProgress);
        return await _shellCatalogService.SyncCatalogAndReloadAsync(
            Session.StoreCode,
            forceFullDownload,
            progress,
            cancellationToken);
    }

    private void ApplyCatalogDownloadProgress(CatalogSyncProgress progress)
    {
        _catalogDownloadHideTimer.Stop();
        IsCatalogDownloadProgressVisible = true;
        IsCatalogDownloadProgressFailed = progress.Stage == CatalogSyncProgressStage.Failed;
        CatalogDownloadProgressValue = progress.Percent;

        if (progress.Stage == CatalogSyncProgressStage.Failed)
        {
            CatalogDownloadProgressText = string.Format(
                _localization.CurrentCulture,
                _localization.T("shell.catalogDownload.failed"),
                progress.Percent);
            CatalogDownloadProgressDetailText = progress.ErrorMessage ?? string.Empty;
            StartCatalogDownloadHideTimer(TimeSpan.FromSeconds(15));
            return;
        }

        var titleKey = progress.Stage == CatalogSyncProgressStage.Completed
            ? "shell.catalogDownload.completed"
            : "shell.catalogDownload.downloading";
        CatalogDownloadProgressText = string.Format(
            _localization.CurrentCulture,
            _localization.T(titleKey),
            progress.Percent);
        CatalogDownloadProgressDetailText = string.Format(
            _localization.CurrentCulture,
            _localization.T("shell.catalogDownload.detail"),
            progress.DownloadedCount,
            progress.TotalCount,
            progress.RemotePages,
            progress.UpsertedCount,
            progress.DeletedCount,
            FormatElapsed(progress.ElapsedMilliseconds));

        if (progress.Stage == CatalogSyncProgressStage.Completed)
        {
            StartCatalogDownloadHideTimer(TimeSpan.FromSeconds(5));
        }
    }

    private void StartCatalogDownloadHideTimer(TimeSpan interval)
    {
        _catalogDownloadHideTimer.Interval = interval;
        _catalogDownloadHideTimer.Start();
    }

    private string FormatElapsed(long elapsedMilliseconds)
    {
        return string.Format(
            _localization.CurrentCulture,
            _localization.T("shell.catalogDownload.elapsedSeconds"),
            elapsedMilliseconds / 1000d);
    }

    private async Task<IReadOnlyList<SellableItemDto>> ReloadCatalogIndexAsync(CancellationToken cancellationToken = default)
    {
        return await ReloadCatalogIndexAsync(Session.StoreCode, cancellationToken);
    }

    private async Task<IReadOnlyList<SellableItemDto>> ReloadCatalogIndexAsync(string storeCode, CancellationToken cancellationToken)
    {
        return await _shellCatalogService.LoadLocalCatalogAsync(storeCode, cancellationToken);
    }

    private async Task BeginDeviceReregistrationAsync()
    {
        if (_startupOptions?.PreviewMode == true)
        {
            StatusMessage = "Preview mode does not support device reregistration.";
            return;
        }

        if (!_cart.IsEmpty)
        {
            StatusMessage = "请先完成或清空当前购物车后再重新注册设备。";
            return;
        }

        var syncSnapshot = await _shellSyncCenterService.GetSnapshotAsync();
        var overview = syncSnapshot.Overview;
        if (overview.PendingCount > 0 || overview.FailedCount > 0 || overview.SyncingCount > 0)
        {
            StatusMessage = "存在待同步、失败或同步中的订单，请先处理同步后再重新注册设备。";
            ApplySyncCenterSnapshot(syncSnapshot);
            return;
        }

        var startupOptions = _startupOptions ?? new AppStartupOptions([], false, null, null);
        DeviceRegistration = CreateDeviceRegistrationViewModel(startupOptions);
        _pendingDeviceRegistrationCache = null;
        _deviceRegistrationStoreLoadTask = null;
        DeviceRegistration.PrepareReregister(Session.StoreCode);
        CurrentScreen = DeviceRegistration;
        _deviceRegistrationStoreLoadTask = DeviceRegistration.LoadStoresAsync(null);
        await _deviceRegistrationStoreLoadTask;
    }

    private void ApplyDeviceReregistered()
    {
        _mainShellStartupService.ClearAuthorization();
        _posPostShowStartupTask = null;
        PosTerminal?.Dispose();
        SpecialProducts?.Dispose();
        PosTerminal = null;
        SpecialProducts = null;
        CashPayment = null;
        TransactionHistory = null;
        _lastCompletedOrder = null;
        _cart.Clear();
        SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Closed, Application.Current?.MainWindow);
        StatusMessage = "设备重新注册申请已提交，旧注册信息已禁用，请等待审批后重新检查。";
    }

    private void CancelDeviceReregistration()
    {
        if (PosTerminal is null)
        {
            return;
        }

        DeviceRegistration = null;
        _deviceRegistrationStoreLoadTask = null;
        ShowPos();
        StatusMessage = "已取消重新注册设备。";
    }

    private void OnCartChanged(object? sender, EventArgs e)
    {
        CashPayment?.RefreshCart();
        LoadCustomerDisplayFromCart();
        ShowCashPaymentCommand.NotifyCanExecuteChanged();
    }

    private void ShowPos()
    {
        if (PosTerminal is null)
        {
            return;
        }

        CurrentScreen = PosTerminal;
    }

    private Task ShowSpecialProductsAsync()
    {
        if (SpecialProducts is null)
        {
            return Task.CompletedTask;
        }

        SpecialProducts.Session = Session;
        CurrentScreen = SpecialProducts;
        _ = EnsureSpecialProductsLoadedAsync(SpecialProducts);
        return Task.CompletedTask;
    }

    private static async Task EnsureSpecialProductsLoadedAsync(SpecialProductsViewModel specialProducts)
    {
        try
        {
            await specialProducts.EnsureLoadedAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("SpecialProducts", $"background load failed error={ex.Message}");
        }
    }

    private void ShowCashPayment()
    {
        if (_cart.IsEmpty)
        {
            ShowPos();
            return;
        }

        CashPayment = new CashPaymentViewModel(
            _cart,
            _cashPaymentWorkflowService,
            Session,
            _localization);
        CashPayment.PaymentCancelled += (_, _) => ShowPos();
        CashPayment.PaymentCompleted += OnPaymentCompleted;
        CurrentScreen = CashPayment;
    }

    private async void OnPaymentCompleted(object? sender, PaymentCompletedEventArgs e)
    {
        _lastCompletedOrder = e.Order;
        await RefreshPendingSyncAsync();
        PaymentSuccess.LoadFromOrder(e.Order);
        CurrentScreen = PaymentSuccess;

        ShowCashPaymentCommand.NotifyCanExecuteChanged();
    }

    private async Task ShowPaymentSuccessLatestAsync()
    {
        if (_lastCompletedOrder is not null)
        {
            PaymentSuccess.LoadFromOrder(_lastCompletedOrder);
        }
        else
        {
            await PaymentSuccess.LoadLatestAsync();
        }

        CurrentScreen = PaymentSuccess;
    }

    private async Task ShowHistoryAsync()
    {
        TransactionHistory ??= CreateTransactionHistoryViewModel();
        await TransactionHistory.LoadAsync();
        CurrentScreen = TransactionHistory;
    }

    private async Task ShowSuspendedHistoryAsync()
    {
        TransactionHistory ??= CreateTransactionHistoryViewModel();
        await TransactionHistory.ShowSuspendedOrdersAsync();
        CurrentScreen = TransactionHistory;
    }

    private async Task SuspendCurrentOrderAsync()
    {
        if (_suspendedOrderService is null)
        {
            StatusMessage = "挂单服务不可用。";
            return;
        }

        try
        {
            var suspended = await _suspendedOrderService.SuspendCurrentOrderAsync(Session);
            PosTerminal?.RefreshCart();
            CashPayment?.RefreshCart();
            StatusMessage = $"已挂单 #{suspended.SuspendedOrderGuid.ToString("N")[..8].ToUpperInvariant()}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private TransactionHistoryViewModel CreateTransactionHistoryViewModel()
    {
        return new TransactionHistoryViewModel(
            _receiptQueryService,
            _suspendedOrderService,
            _remoteOrderHistoryService,
            Session,
            OnSuspendedOrderRecalledAsync);
    }

    private Task OnSuspendedOrderRecalledAsync()
    {
        PosTerminal?.RefreshCart();
        CashPayment?.RefreshCart();
        ShowPos();
        StatusMessage = "挂单已取回。";
        return Task.CompletedTask;
    }

    private void ShowCustomerDisplay()
    {
        LoadCustomerDisplayFromCart();
        CurrentScreen = CustomerDisplay;
    }

    private void LoadCustomerDisplayFromCart()
    {
        _customerDisplayOrchestrator.LoadFromCart(CustomerDisplay, Session, _cart);
    }

    private async Task ToggleSyncCenterAsync()
    {
        if (!IsSyncCenterExpanded)
        {
            await RefreshPendingSyncAsync();
        }

        IsSyncCenterExpanded = !IsSyncCenterExpanded;
    }

    private void ToggleCustomerDisplayWindow()
    {
        var owner = Application.Current?.MainWindow;
        if (owner is null)
        {
            return;
        }

        ToggleCustomerDisplayWindow(owner);
    }

    public void ToggleCustomerDisplayWindow(Window? owner)
    {
        var targetMode = _customerDisplayOrchestrator.GetNextMode(CustomerDisplayWindowMode);
        SetCustomerDisplayWindowMode(targetMode, owner);
    }

    private void CloseCustomerDisplayWindow()
    {
        SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Closed, Application.Current?.MainWindow);
    }

    private void ShowCustomerDisplayNormal()
    {
        SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Normal, Application.Current?.MainWindow);
    }

    private void ShowCustomerDisplayFullscreen()
    {
        SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Fullscreen, Application.Current?.MainWindow);
    }

    public void SetCustomerDisplayWindowMode(CustomerDisplayWindowMode mode, Window? owner)
    {
        ApplyCustomerDisplayWindowResult(_customerDisplayOrchestrator.SetMode(mode, CustomerDisplay, Session, _cart, owner));
    }

    private void OpenCustomerDisplayWindow(Window? owner)
    {
        SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Fullscreen, owner);
    }

    private void ApplyCustomerDisplayWindowResult(CustomerDisplayWindowResult result)
    {
        CustomerDisplayWindowMode = result.Mode;
        if (!string.IsNullOrWhiteSpace(result.StatusMessageKey))
        {
            StatusMessage = _localization.T(result.StatusMessageKey);
        }
    }

    private async Task ResetScannerBindingAsync()
    {
        await _rawScannerService.ResetBindingAsync();
        StatusMessage = "扫码枪绑定已清除，请在收银页扫描一次重新学习。";
    }

    private void ResetForNewTransaction()
    {
        _cart.Clear();
        ShowCashPaymentCommand.NotifyCanExecuteChanged();
        ShowPos();
    }

    private void NavigateFromStartup(string? initialScreen)
    {
        switch ((initialScreen ?? "pos").Trim().ToLowerInvariant())
        {
            case "cash":
            case "payment":
                ShowCashPayment();
                break;
            case "success":
                CurrentScreen = PaymentSuccess;
                if (_lastCompletedOrder is not null)
                {
                    PaymentSuccess.LoadFromOrder(_lastCompletedOrder);
                }
                break;
            case "history":
                _ = ShowHistoryAsync();
                break;
            case "customer":
            case "display":
                ShowCustomerDisplay();
                break;
            default:
                ShowPos();
                break;
        }
    }

    private void AddPreviewCartItems(IReadOnlyList<SellableItemDto> items)
    {
        _cart.Clear();
        foreach (var item in items.Take(3))
        {
            _cart.AddItem(item);
        }

        if (items.Count > 1)
        {
            _cart.AddItem(items[1]);
        }

        ShowCashPaymentCommand.NotifyCanExecuteChanged();
    }

    private async Task<LocalOrder> CreatePreviewOrderAsync(IReadOnlyList<SellableItemDto> items)
    {
        var previewCart = new PosCartService();
        foreach (var item in items.Take(2))
        {
            previewCart.AddItem(item);
        }

        if (items.Count > 0)
        {
            previewCart.AddItem(items[0]);
        }

        var result = _checkout.CreateCashOrder(previewCart, Session, previewCart.ActualAmount);
        await _orderRepository.SavePendingOrderAsync(result.Order);
        PaymentSuccess.LoadFromOrder(result.Order);
        return result.Order;
    }

    private static IReadOnlyList<SellableItemDto> CreateStarterItems()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new(DefaultTestStoreCode, "SKU-001", null, "Organic Fuji Apple", "690001", "SKU-001", "690001", 4.50m, PriceSourceKind.StoreRetailPrice, "Store Price", 1m, now),
            new(DefaultTestStoreCode, "SKU-002", null, "Whole Milk 1L", "690002", "SKU-002", "690002", 3.20m, PriceSourceKind.ProductBase, "Base Price", 1m, now),
            new(DefaultTestStoreCode, "SKU-003", "SET-003", "Greek Yogurt Blueberry", "690003", "SKU-003", "690003", 3.75m, PriceSourceKind.StoreMultiCodeProduct, "Multi-code Store Price", 1m, now),
            new(DefaultTestStoreCode, "SKU-004", "CLR-004", "Cold Brew Concentrate", "690004", "SKU-004", "690004", 12.90m, PriceSourceKind.StoreClearancePrice, "Clearance Price", 1m, now)
        ];
    }
}

internal static class ObservableCollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }
}
