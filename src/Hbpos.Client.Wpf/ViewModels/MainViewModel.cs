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
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed record DeviceReregistrationStartResult(bool Started, string StatusMessage)
{
    public static DeviceReregistrationStartResult StartedWith(string statusMessage) => new(true, statusMessage);

    public static DeviceReregistrationStartResult Blocked(string statusMessage) => new(false, statusMessage);
}

public sealed partial class MainViewModel : ObservableObject
{
    private const string DefaultTestStoreCode = "1002";
    private static readonly TimeSpan StartupCatalogIndexLoadTimeout = TimeSpan.FromSeconds(30);

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
    private readonly IOrderUploadExecutionService _orderUploadExecutionService;
    private readonly ILocalizationService _localization;
    private readonly ICustomerDisplayOrchestrator _customerDisplayOrchestrator;
    private readonly IRawScannerService _rawScannerService;
    private readonly IUserFeedbackService _userFeedbackService;
    private readonly IReceiptQueryService _receiptQueryService;
    private readonly IReceiptPrintService _receiptPrintService;
    private readonly IReceiptPrinterSettingsStore? _receiptPrinterSettingsStore;
    private readonly IReceiptTextFormatter _receiptTextFormatter;
    private readonly IInstallmentOrderService _installmentOrderService;
    private readonly ISuspendedOrderService? _suspendedOrderService;
    private readonly IRemoteOrderHistoryService? _remoteOrderHistoryService;
    private readonly ICashPaymentWorkflowService _cashPaymentWorkflowService;
    private readonly IVoucherApiClient? _voucherApiClient;
    private readonly ICardTerminalClient? _cardTerminalClient;
    private readonly ICardTerminalSetupService? _cardTerminalSetupService;
    private readonly IDeviceRegistrationWorkflowService _deviceRegistrationWorkflowService;
    private readonly ISpecialProductsWorkflowService _specialProductsWorkflowService;
    private readonly IReceiptReturnsWorkflowService _receiptReturnsWorkflowService;
    private readonly IDailyCloseService _dailyCloseService;
    private readonly IDailyClosePrintService _dailyClosePrintService;
    private readonly ICashDrawerService _cashDrawerService;
    private readonly IApplicationExitService _applicationExitService;
    private readonly IConfirmationDialogService _confirmationDialogService;
    private readonly ITestSalesDataResetService? _testSalesDataResetService;
    private readonly ILinklyTerminalDialogPresenter? _linklyTerminalDialogPresenter;
    private readonly ICardPaymentRecoveryService? _cardPaymentRecoveryService;
    private readonly ICardRecoveryResultDialogService? _cardRecoveryResultDialogService;
    private readonly PosTerminalWorkflowFactory _posTerminalWorkflowFactory;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _connectivityTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _catalogDownloadHideTimer = new();

    private bool _isApplyingCulture;
    private bool _isRefreshingConnectivity;
    private bool _isAutoOrderSyncRetrying;
    private bool _schemaReady;
    private LocalOrder? _lastCompletedOrder;
    private LocalDeviceCache? _pendingDeviceRegistrationCache;
    private CancellationTokenSource? _startupCatalogIndexLoadCts;
    private Task? _deviceRegistrationStoreLoadTask;
    private Task? _posPostShowStartupTask;
    private Task<CardPaymentRecoveryResult>? _cardPaymentRecoveryTask;
    private ReceiptDetails? _cardRecoveryDialogReceipt;
    private bool _customerDisplayPrewarmed;
    private Task<IReadOnlyList<SellableItemDto>>? _startupCatalogIndexLoadTask;
    private AppStartupOptions? _startupOptions;

    [ObservableProperty]
    private PosSessionState _session = new("HB POS", DefaultTestStoreCode, "Main Branch", "Terminal 04", "C001", "Alice", false, 0);

    [ObservableProperty]
    private object? _currentScreen;

    [ObservableProperty]
    private PosTerminalViewModel? _cachedPosTerminalScreen;

    [ObservableProperty]
    private PaymentViewModel? _cachedCashPaymentScreen;

    [ObservableProperty]
    private SpecialProductsViewModel? _cachedSpecialProductsScreen;

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
    private bool _isDeviceReregistrationDialogOpen;

    [ObservableProperty]
    private bool _isCardRecoveryResultDialogOpen;

    [ObservableProperty]
    private CardRecoveryResultDialogViewModel? _cardRecoveryResultDialog;

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

    [ObservableProperty]
    private bool _isOrderSyncRetrying;

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
        IRawScannerService rawScannerService,
        IUserFeedbackService? userFeedbackService = null,
        IReceiptPrintService? receiptPrintService = null,
        IReceiptPrinterSettingsStore? receiptPrinterSettingsStore = null,
        IReceiptTextFormatter? receiptTextFormatter = null,
        IOrderUploadExecutionService? orderUploadExecutionService = null,
        IDailyCloseService? dailyCloseService = null,
        IDailyClosePrintService? dailyClosePrintService = null,
        ICashDrawerService? cashDrawerService = null,
        IApplicationExitService? applicationExitService = null,
        IConfirmationDialogService? confirmationDialogService = null,
        ITestSalesDataResetService? testSalesDataResetService = null)
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
                reloadCatalogAsync),
            receiptReturnsWorkflowService: new ReceiptReturnsWorkflowService(
                new ReceiptQueryService(orderRepository),
                orderRepository,
                null,
                priceIndex,
                cart,
                localization),
            installmentOrderService: NoopInstallmentOrderService.Instance,
            userFeedbackService: userFeedbackService ?? NoopUserFeedbackService.Instance,
            receiptPrintService: receiptPrintService ?? new NoopReceiptPrintService(localization),
            receiptPrinterSettingsStore: receiptPrinterSettingsStore,
            receiptTextFormatter: receiptTextFormatter ?? new ReceiptTextFormatter(),
            orderUploadExecutionService: orderUploadExecutionService,
            dailyCloseService: dailyCloseService,
            dailyClosePrintService: dailyClosePrintService,
            cashDrawerService: cashDrawerService,
            applicationExitService: applicationExitService,
            confirmationDialogService: confirmationDialogService,
            testSalesDataResetService: testSalesDataResetService)
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
        IRemoteOrderHistoryService? remoteOrderHistoryService = null,
        IUserFeedbackService? userFeedbackService = null,
        IReceiptReturnsWorkflowService? receiptReturnsWorkflowService = null,
        IVoucherApiClient? voucherApiClient = null,
        ICardTerminalClient? cardTerminalClient = null,
        ICardTerminalSetupService? cardTerminalSetupService = null,
        IReceiptPrintService? receiptPrintService = null,
        IReceiptPrinterSettingsStore? receiptPrinterSettingsStore = null,
        IReceiptTextFormatter? receiptTextFormatter = null,
        IOrderUploadExecutionService? orderUploadExecutionService = null,
        IDailyCloseService? dailyCloseService = null,
        IDailyClosePrintService? dailyClosePrintService = null,
        ICashDrawerService? cashDrawerService = null,
        IApplicationExitService? applicationExitService = null,
        IConfirmationDialogService? confirmationDialogService = null,
        IInstallmentOrderService? installmentOrderService = null,
        ITestSalesDataResetService? testSalesDataResetService = null,
        ILinklyTerminalDialogPresenter? linklyTerminalDialogPresenter = null,
        ICardPaymentRecoveryService? cardPaymentRecoveryService = null,
        ICardRecoveryResultDialogService? cardRecoveryResultDialogService = null)
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
        _orderUploadExecutionService = orderUploadExecutionService ?? NoopOrderUploadExecutionService.Instance;
        _localization = localization;
        _customerDisplayOrchestrator = customerDisplayOrchestrator;
        _rawScannerService = rawScannerService;
        _userFeedbackService = userFeedbackService ?? NoopUserFeedbackService.Instance;
        _receiptQueryService = receiptQueryService;
        _receiptPrintService = receiptPrintService ?? new NoopReceiptPrintService(_localization);
        _receiptPrinterSettingsStore = receiptPrinterSettingsStore;
        _receiptTextFormatter = receiptTextFormatter ?? new ReceiptTextFormatter();
        _installmentOrderService = installmentOrderService ?? NoopInstallmentOrderService.Instance;
        _suspendedOrderService = suspendedOrderService;
        _remoteOrderHistoryService = remoteOrderHistoryService;
        _cashPaymentWorkflowService = cashPaymentWorkflowService;
        _voucherApiClient = voucherApiClient;
        _cardTerminalClient = cardTerminalClient;
        _cardTerminalSetupService = cardTerminalSetupService;
        _deviceRegistrationWorkflowService = deviceRegistrationWorkflowService;
        _specialProductsWorkflowService = specialProductsWorkflowService;
        _receiptReturnsWorkflowService = receiptReturnsWorkflowService ?? new ReceiptReturnsWorkflowService(
            _receiptQueryService,
            _orderRepository,
            _remoteOrderHistoryService,
            _priceIndex,
            _cart,
            _localization);
        _dailyCloseService = dailyCloseService ?? NoopDailyCloseService.Instance;
        _dailyClosePrintService = dailyClosePrintService ?? NoopDailyClosePrintService.Instance;
        _cashDrawerService = cashDrawerService ?? new NoopCashDrawerService(_localization);
        _applicationExitService = applicationExitService ?? new WpfApplicationExitService();
        _confirmationDialogService = confirmationDialogService ?? new WpfConfirmationDialogService();
        _testSalesDataResetService = testSalesDataResetService;
        _linklyTerminalDialogPresenter = linklyTerminalDialogPresenter;
        _cardPaymentRecoveryService = cardPaymentRecoveryService;
        _cardRecoveryResultDialogService = cardRecoveryResultDialogService;
        _posTerminalWorkflowFactory = posTerminalWorkflowFactory;

        PaymentSuccess = new PaymentSuccessViewModel(
            _receiptQueryService,
            _receiptTextFormatter,
            _receiptPrinterSettingsStore);
        PaymentSuccess.NewTransactionRequested += (_, _) => ResetForNewTransaction();
        PaymentSuccess.PrintReceiptRequested += async (_, _) => await PrintPaymentSuccessReceiptAsync();

        ShowPosCommand = new RelayCommand(ShowPos);
        ShowCashPaymentCommand = new RelayCommand(ShowCashPayment, () => !_cart.IsEmpty);
        ShowReturnsCommand = new RelayCommand(ShowReturns);
        ShowPaymentSuccessCommand = new AsyncRelayCommand(ShowPaymentSuccessLatestAsync);
        ShowHistoryCommand = new AsyncRelayCommand(ShowHistoryAsync);
        ShowDailyCloseCommand = new AsyncRelayCommand(ShowDailyCloseAsync);
        ShowCustomerDisplayCommand = new RelayCommand(ShowCustomerDisplay);
        ShowSettingsCommand = new AsyncRelayCommand(ShowSettingsAsync);
        ToggleSyncCenterCommand = new AsyncRelayCommand(ToggleSyncCenterAsync);
        RetrySyncOrderCommand = new AsyncRelayCommand<SyncQueueListItem?>(RetrySyncOrderAsync, CanRetrySyncOrder);
        RetryAllSyncOrdersCommand = new AsyncRelayCommand(RetryAllSyncOrdersAsync, CanRetryAllSyncOrders);
        ToggleCustomerDisplayWindowCommand = new RelayCommand(ToggleCustomerDisplayWindow);
        CloseCustomerDisplayWindowCommand = new RelayCommand(CloseCustomerDisplayWindow);
        ShowCustomerDisplayNormalCommand = new RelayCommand(ShowCustomerDisplayNormal);
        ShowCustomerDisplayFullscreenCommand = new RelayCommand(ShowCustomerDisplayFullscreen);
        ToggleCultureCommand = new AsyncRelayCommand(ToggleCultureAsync);
        ResetScannerBindingCommand = new AsyncRelayCommand(ResetScannerBindingAsync);
        CloseCardRecoveryResultDialogCommand = new RelayCommand(CloseCardRecoveryResultDialog);
        PrintRecoveredReceiptCommand = new AsyncRelayCommand(PrintRecoveredReceiptAsync, CanPrintRecoveredReceipt);

        if (_cardRecoveryResultDialogService is not null)
        {
            _cardRecoveryResultDialogService.DialogRequested += OnCardRecoveryResultDialogRequested;
        }

        _cart.CartChanged += OnCartChanged;
        _localization.CultureChanged += OnCultureChanged;
        _customerDisplayOrchestrator.Closed += (_, _) => CustomerDisplayWindowMode = CustomerDisplayWindowMode.Closed;
        _clockTimer.Tick += (_, _) => RefreshClock();
        _connectivityTimer.Tick += async (_, _) => await RefreshOnlineStateAsync(CancellationToken.None, autoRetryOrders: true);
        _catalogDownloadHideTimer.Tick += (_, _) =>
        {
            _catalogDownloadHideTimer.Stop();
            IsCatalogDownloadProgressVisible = false;
        };
        RefreshLocalizedShell(resetStatus: true);
    }

    public PosTerminalViewModel? PosTerminal { get; private set; }

    public SpecialProductsViewModel? SpecialProducts { get; private set; }

    public PaymentViewModel? CashPayment { get; private set; }

    public InstallmentCenterViewModel? InstallmentCenter { get; private set; }

    public InstallmentCreateViewModel? InstallmentCreate { get; private set; }

    public ReceiptReturnsViewModel? ReceiptReturns { get; private set; }

    public PaymentSuccessViewModel PaymentSuccess { get; }

    public TransactionHistoryViewModel? TransactionHistory { get; private set; }

    public DailyCloseViewModel? DailyClose { get; private set; }

    public CustomerDisplayViewModel CustomerDisplay { get; } = new();

    private DeviceRegistrationViewModel? _deviceRegistration;

    public DeviceRegistrationViewModel? DeviceRegistration
    {
        get => _deviceRegistration;
        private set => SetProperty(ref _deviceRegistration, value);
    }

    public SettingsViewModel? Settings { get; private set; }

    public ILinklyTerminalDialogPresenter? LinklyTerminalDialog => _linklyTerminalDialogPresenter;

    public bool IsPosTerminalScreenActive => ReferenceEquals(CurrentScreen, CachedPosTerminalScreen);

    public bool IsCashPaymentScreenActive => ReferenceEquals(CurrentScreen, CachedCashPaymentScreen);

    public bool IsSpecialProductsScreenActive => ReferenceEquals(CurrentScreen, CachedSpecialProductsScreen);

    public bool IsFallbackScreenActive => CurrentScreen is not null &&
        !IsPosTerminalScreenActive &&
        !IsCashPaymentScreenActive &&
        !IsSpecialProductsScreenActive;

    public string ActivePageTitleText => GetActivePageTitleText();

    public ObservableCollection<SyncQueueListItem> SyncCenterOrders { get; } = [];

    public IRelayCommand ShowPosCommand { get; }

    public IRelayCommand CloseCardRecoveryResultDialogCommand { get; }

    public IAsyncRelayCommand PrintRecoveredReceiptCommand { get; }

    public IRelayCommand ShowCashPaymentCommand { get; }

    public IRelayCommand ShowReturnsCommand { get; }

    public IAsyncRelayCommand ShowPaymentSuccessCommand { get; }

    public IAsyncRelayCommand ShowHistoryCommand { get; }

    public IAsyncRelayCommand ShowDailyCloseCommand { get; }

    public IRelayCommand ShowCustomerDisplayCommand { get; }

    public IAsyncRelayCommand ShowSettingsCommand { get; }

    public IAsyncRelayCommand ToggleSyncCenterCommand { get; }

    public IAsyncRelayCommand<SyncQueueListItem?> RetrySyncOrderCommand { get; }

    public IAsyncRelayCommand RetryAllSyncOrdersCommand { get; }

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
        if (CurrentScreen is IScannerInputTarget scannerInputTarget)
        {
            return scannerInputTarget.ProcessScannerBarcode(
                barcode,
                "keyboard-focus-fallback",
                "keyboard-fallback");
        }

        ConsoleLog.Write(
            "RawScanner",
            $"keyboard fallback scan ignored because active screen cannot handle scanner screen={CurrentScreen?.GetType().Name ?? "<none>"} barcode={barcode}");
        return true;
    }

    private async Task ActivateDeviceAsync(DeviceActivatedEventArgs args, AppStartupOptions startupOptions)
    {
        _deviceRegistrationStoreLoadTask = null;
        _posPostShowStartupTask = null;
        if (DeviceRegistration is not null)
        {
            DeviceRegistration.IsBusy = true;
            DeviceRegistration.StatusMessage = _localization.T("startup.stage.loadingProducts");
        }

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
        IsDeviceReregistrationDialogOpen = false;
        DeviceRegistration = null;
        _ = ContinuePosStartupAfterShownAsync(startupOptions, Application.Current.MainWindow);
    }

    private async Task InitializePosExperienceAsync(AppStartupOptions startupOptions)
    {
        PosTerminal?.Dispose();
        SpecialProducts?.Dispose();
        ReceiptReturns?.Dispose();
        CachedPosTerminalScreen = null;
        ClearCashPaymentCache();
        CachedSpecialProductsScreen = null;
        _customerDisplayPrewarmed = false;
        CancelStartupCatalogIndexLoad();
        IReadOnlyList<SellableItemDto> cachedItems = [];
        if (startupOptions.PreviewMode)
        {
            cachedItems = CreateStarterItems();
            await _shellCatalogService.ReplacePreviewCatalogAsync(cachedItems);
        }

        var posWorkflowService = _posTerminalWorkflowFactory(RefreshRemoteLookupAsync, ReloadCatalogIndexAsync);
        PosTerminal = new PosTerminalViewModel(
            _priceIndex,
            _cart,
            Session,
            ShowCashPayment,
            ShowSpecialProductsAsync,
            _localization,
            userFeedbackService: _userFeedbackService,
            onHoldOrderAsync: SuspendCurrentOrderAsync,
            onRecallOrderAsync: ShowSuspendedHistoryAsync,
            onOpenHistoryAsync: ShowHistoryAsync,
            onOpenDailyCloseAsync: ShowDailyCloseAsync,
            onOpenSettingsAsync: ShowSettingsAsync,
            onOpenCustomerDisplay: ShowCustomerDisplay,
            syncCatalogAsync: SyncCatalogAndReloadAsync,
            resetCatalogAsync: ResetCatalogAndReloadAsync,
            refreshOnlineAsync: RefreshOnlineStateAsync,
            rawScannerService: _rawScannerService,
            onReregisterDeviceAsync: BeginDeviceReregistrationFromPosAsync,
            workflowService: posWorkflowService,
            onOpenReturns: ShowReturns,
            onPrintLastReceiptAsync: PrintLatestReceiptAsync,
            onOpenCashDrawerAsync: OpenCashDrawerAsync,
            onExitApplicationAsync: ExitApplicationAsync);
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
        CachedPosTerminalScreen = PosTerminal;
        ReceiptReturns = new ReceiptReturnsViewModel(
            _receiptReturnsWorkflowService,
            Session,
            ShowPos,
            line => PosTerminal?.RevealCartLine(line),
            _rawScannerService,
            _localization);
        if (cachedItems.Count > 0)
        {
            PosTerminal.LoadMatches(cachedItems);
        }

        TransactionHistory = CreateTransactionHistoryViewModel();

        if (startupOptions.PreviewMode)
        {
            AddPreviewCartItems(cachedItems);
            _lastCompletedOrder = await CreatePreviewOrderAsync(cachedItems);
        }

        await RefreshPendingSyncAsync();
        RefreshClock();
        _clockTimer.Start();
        ApplySessionToScreens();
        PrepareCachedCashPaymentScreen();
        // 诊断启动卡顿时先关闭客显预热，避免启动阶段创建隐藏窗口。
        ConsoleLog.Write(
            "CustomerDisplay",
            $"startup prewarm skipped store={Session.StoreCode} device={Session.DeviceCode} reason=auto-open-disabled");
        await BeginStartupCatalogIndexLoadAsync(startupOptions);
        await PreloadStartupSpecialProductsDataAsync(startupOptions);
        NavigateFromStartup(startupOptions.InitialScreen);
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
        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"post-show open start store={Session.StoreCode} device={Session.DeviceCode} ownerPresent={owner is not null}");
        try
        {
            // 诊断启动卡顿时关闭客显自动打开；手动客显按钮仍可正常打开。
            ConsoleLog.Write(
                "CustomerDisplay",
                $"post-show open skipped store={Session.StoreCode} device={Session.DeviceCode} ownerPresent={owner is not null} reason=auto-open-disabled");
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"post-show open completed store={Session.StoreCode} device={Session.DeviceCode} displayMode={CustomerDisplayWindowMode} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"post-show open failed store={Session.StoreCode} device={Session.DeviceCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }

        ConsoleLog.Write(
            "SpecialProducts",
            $"startup home preload skipped store={Session.StoreCode} reason=moved-before-main-window");
        await RecoverCardPaymentAttemptAsync(navigateToPaymentOnDraft: false);
        await RefreshOnlineStateAsync(CancellationToken.None, autoRetryOrders: true);
        _connectivityTimer.Start();
        BeginInitialCatalogSync();
    }

    private async Task<IReadOnlyList<SellableItemDto>> LoadStartupCatalogIndexAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write("CatalogStartup", $"local catalog load start store={Session.StoreCode}");
        try
        {
            var cachedItems = await _shellCatalogService.LoadLocalCatalogAsync(Session.StoreCode, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            PosTerminal?.LoadMatches(cachedItems);
            PosTerminal?.RefreshCart();
            CashPayment?.RefreshCart();
            stopwatch.Stop();
            ConsoleLog.Write("CatalogStartup", $"local catalog load completed store={Session.StoreCode} items={cachedItems.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return cachedItems;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            ConsoleLog.Write("CatalogStartup", $"local catalog load canceled store={Session.StoreCode} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return [];
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write("CatalogStartup", $"local catalog load failed store={Session.StoreCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            StatusMessage = ex.Message;
            return [];
        }
    }

    private async Task<IReadOnlyList<SellableItemDto>> BeginStartupCatalogIndexLoadAsync(AppStartupOptions startupOptions)
    {
        if (startupOptions.PreviewMode)
        {
            return [];
        }

        _startupCatalogIndexLoadCts ??= new CancellationTokenSource();
        _startupCatalogIndexLoadCts.CancelAfter(StartupCatalogIndexLoadTimeout);
        _startupCatalogIndexLoadTask ??= LoadStartupCatalogIndexAsync(_startupCatalogIndexLoadCts.Token);
        var cts = _startupCatalogIndexLoadCts;
        var loadTask = _startupCatalogIndexLoadTask;
        try
        {
            return await loadTask;
        }
        finally
        {
            if (ReferenceEquals(_startupCatalogIndexLoadCts, cts))
            {
                _startupCatalogIndexLoadCts = null;
            }

            if (ReferenceEquals(_startupCatalogIndexLoadTask, loadTask))
            {
                _startupCatalogIndexLoadTask = null;
            }

            cts.Dispose();
        }
    }

    private void CancelStartupCatalogIndexLoad()
    {
        var cts = _startupCatalogIndexLoadCts;
        _startupCatalogIndexLoadCts = null;
        _startupCatalogIndexLoadTask = null;
        cts?.Cancel();
    }

    partial void OnSessionChanged(PosSessionState value)
    {
        RefreshLocalizedShell();
        ApplySessionToScreens();
    }

    partial void OnPendingUploadCountChanged(int value)
    {
        RefreshSyncRetryCommandStates();
    }

    partial void OnFailedUploadCountChanged(int value)
    {
        RefreshSyncRetryCommandStates();
    }

    partial void OnIsOrderSyncRetryingChanged(bool value)
    {
        RefreshSyncRetryCommandStates();
    }

    partial void OnCurrentScreenChanged(object? value)
    {
        if (!ReferenceEquals(value, ReceiptReturns))
        {
            ReceiptReturns?.ResetToDefault();
        }

        RaiseScreenHostStateChanged();
        _rawScannerService.SetActivePage((value as IScannerInputTarget)?.ScannerPageId);
    }

    partial void OnCachedPosTerminalScreenChanged(PosTerminalViewModel? value)
    {
        RaiseScreenHostStateChanged();
    }

    partial void OnCachedCashPaymentScreenChanged(PaymentViewModel? value)
    {
        RaiseScreenHostStateChanged();
    }

    partial void OnCachedSpecialProductsScreenChanged(SpecialProductsViewModel? value)
    {
        RaiseScreenHostStateChanged();
    }

    private void RaiseScreenHostStateChanged()
    {
        OnPropertyChanged(nameof(IsPosTerminalScreenActive));
        OnPropertyChanged(nameof(IsCashPaymentScreenActive));
        OnPropertyChanged(nameof(IsSpecialProductsScreenActive));
        OnPropertyChanged(nameof(IsFallbackScreenActive));
        OnPropertyChanged(nameof(ActivePageTitleText));
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
        OnPropertyChanged(nameof(ActivePageTitleText));
        if (resetStatus || string.IsNullOrWhiteSpace(StatusMessage))
        {
            StatusMessage = _localization.T("StatusOfflineReady");
        }
    }

    private string GetActivePageTitleText()
    {
        if (ReferenceEquals(CurrentScreen, InstallmentCenter))
        {
            return InstallmentCenter?.PageTitleText ?? "分期中心";
        }

        if (ReferenceEquals(CurrentScreen, InstallmentCreate))
        {
            return InstallmentCreate?.PageTitleText ?? "创建分期";
        }

        return _localization.T(GetActivePageTitleKey());
    }

    private string GetActivePageTitleKey()
    {
        if (ReferenceEquals(CurrentScreen, PosTerminal))
        {
            return "shell.page.pos";
        }

        if (ReferenceEquals(CurrentScreen, CashPayment))
        {
            return CashPayment?.PaymentMode switch
            {
                PaymentEntryMode.Refund => "shell.page.refund",
                PaymentEntryMode.ZeroSettlement => "shell.page.zeroSettlement",
                _ => "shell.page.payment"
            };
        }

        if (ReferenceEquals(CurrentScreen, SpecialProducts))
        {
            return "shell.page.specialProducts";
        }

        if (ReferenceEquals(CurrentScreen, ReceiptReturns))
        {
            return "shell.page.returns";
        }

        if (ReferenceEquals(CurrentScreen, PaymentSuccess))
        {
            return "shell.page.paymentSuccess";
        }

        if (ReferenceEquals(CurrentScreen, TransactionHistory))
        {
            return "shell.page.history";
        }

        if (ReferenceEquals(CurrentScreen, DailyClose))
        {
            return "shell.page.dailyClose";
        }

        if (ReferenceEquals(CurrentScreen, Settings))
        {
            return "shell.page.settings";
        }

        if (ReferenceEquals(CurrentScreen, CustomerDisplay))
        {
            return "shell.page.customerDisplay";
        }

        if (ReferenceEquals(CurrentScreen, DeviceRegistration))
        {
            return "shell.page.deviceRegistration";
        }

        return "shell.page.loading";
    }

    private async Task RefreshPendingSyncAsync()
    {
        ApplySyncCenterSnapshot(await _shellSyncCenterService.GetSnapshotAsync());
    }

    private DeviceRegistrationViewModel CreateDeviceRegistrationViewModel(AppStartupOptions startupOptions)
    {
        var viewModel = new DeviceRegistrationViewModel(_deviceRegistrationWorkflowService, _localization);
        viewModel.DeviceActivatedAsync += (_, args) => ActivateDeviceAsync(args, startupOptions);
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

        if (ReceiptReturns is not null)
        {
            ReceiptReturns.Session = Session;
        }

        if (TransactionHistory is not null)
        {
            TransactionHistory.Session = Session;
        }

        if (DailyClose is not null)
        {
            DailyClose.Session = Session;
        }

        InstallmentCenter?.Prepare(Session, CreateCurrentCartSnapshot());
        if (InstallmentCreate is not null)
        {
            InstallmentCreate.Session = Session;
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

    private void BeginSpecialProductsHomePreload()
    {
        if (SpecialProducts is null)
        {
            return;
        }

        PrepareCachedSpecialProductsScreen();
        _ = TryPreloadSpecialProductsHomeAsync();
    }

    private async Task PreloadStartupSpecialProductsDataAsync(AppStartupOptions startupOptions)
    {
        if (startupOptions.PreviewMode || SpecialProducts is null)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write("SpecialProducts", $"startup data preload start store={Session.StoreCode}");
        try
        {
            PrepareCachedSpecialProductsScreen();
            // 启动阶段只预加载特殊商品数据，图片缩略图留给页面进入后异步加载。
            await SpecialProducts.PreloadAsync(CancellationToken.None);
            stopwatch.Stop();
            ConsoleLog.Write(
                "SpecialProducts",
                $"startup data preload completed store={Session.StoreCode} items={SpecialProducts.SpecialItems.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "SpecialProducts",
                $"startup data preload failed store={Session.StoreCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
        }
    }

    private void PrepareCachedCashPaymentScreen()
    {
        if (CashPayment is null)
        {
            CashPayment = new PaymentViewModel(
                _cart,
                _cashPaymentWorkflowService,
                Session,
                _localization,
                ShowPos,
                ShowInstallmentCenter,
                RecoverActiveCardPaymentSessionFromPaymentAsync);
            CashPayment.PaymentCompleted += OnPaymentCompleted;
            CashPayment.PropertyChanged += OnCashPaymentPropertyChanged;
        }

        if (ReferenceEquals(CachedCashPaymentScreen, CashPayment))
        {
            return;
        }

        CachedCashPaymentScreen = CashPayment;
    }

    private void PrepareCachedSpecialProductsScreen()
    {
        if (SpecialProducts is null || ReferenceEquals(CachedSpecialProductsScreen, SpecialProducts))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        CachedSpecialProductsScreen = SpecialProducts;
        stopwatch.Stop();
        ConsoleLog.Write(
            "SpecialProducts",
            $"special screen view prepared store={Session.StoreCode} elapsedMs={stopwatch.ElapsedMilliseconds}");
    }

    private async Task TryPreloadSpecialProductsHomeAsync()
    {
        try
        {
            if (SpecialProducts is not null)
            {
                await SpecialProducts.PreloadFirstPageThumbnailsAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("SpecialProducts", $"startup home preload failed store={Session.StoreCode} error={ex.Message}");
        }
    }

    private async Task<bool> RefreshOnlineStateAsync(CancellationToken cancellationToken)
    {
        return await RefreshOnlineStateAsync(cancellationToken, autoRetryOrders: false);
    }

    private async Task<bool> RefreshOnlineStateAsync(CancellationToken cancellationToken, bool autoRetryOrders)
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

            if (isOnline && autoRetryOrders)
            {
                await TryAutoRetryPendingOrdersAsync(cancellationToken);
            }

            return isOnline;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // API 探测失败只代表当前离线，不能阻断已授权设备继续使用本地缓存收银。
            ConsoleLog.Write("Connectivity", $"online check failed; fallback offline error={ex.Message}");
            if (Session.IsOnline)
            {
                Session = Session with { IsOnline = false };
            }

            return false;
        }
        finally
        {
            _isRefreshingConnectivity = false;
        }
    }

    private async Task TryInitialCatalogSyncAsync()
    {
        var hasLocalCatalogItems = await HasLocalCatalogItemsAsync();
        var syncMode = hasLocalCatalogItems ? "background-refresh" : "initial-full-download";
        try
        {
            // 启动后的商品同步已在 POS 显示之后执行，不能再用启动超时截断，避免半包缓存反复无法补全。
            ConsoleLog.Write(
                "CatalogSync",
                $"initial sync mode={syncMode} localItemCount={(hasLocalCatalogItems ? ">0" : "0")} timeoutSeconds=none");
            var cachedItems = await SyncCatalogAndReloadAsync(CancellationToken.None);
            PosTerminal?.LoadMatches(cachedItems);
            PosTerminal?.RefreshCart();
            CashPayment?.RefreshCart();
        }
        catch (OperationCanceledException)
        {
            ConsoleLog.Write("CatalogSync", $"initial sync canceled mode={syncMode} timeoutSeconds=none");
            StatusMessage = hasLocalCatalogItems
                ? _localization.T("main.catalogSync.timedOut")
                : _localization.T("main.catalogSync.initialDownloadTimedOut");
        }
        catch (Exception ex)
        {
            var statusKey = hasLocalCatalogItems
                ? "main.catalogSync.failed"
                : "main.catalogSync.initialDownloadFailed";
            StatusMessage = string.Format(_localization.CurrentCulture, _localization.T(statusKey), ex.Message);
        }
    }

    private async Task<bool> HasLocalCatalogItemsAsync()
    {
        try
        {
            var firstPage = await _catalogRepository.LoadSellableItemComparePageAsync(
                Session.StoreCode,
                afterLookupCodeNormalized: null,
                pageSize: 1);
            return firstPage.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 本地探测失败只影响提示文案，不阻止后台同步继续尝试。
            ConsoleLog.Write("CatalogSync", $"initial sync local cache probe failed store={Session.StoreCode} error={ex.Message}");
            return true;
        }
    }

    private async Task TryAutoRetryPendingOrdersAsync(CancellationToken cancellationToken)
    {
        if (_isAutoOrderSyncRetrying || IsOrderSyncRetrying)
        {
            return;
        }

        _isAutoOrderSyncRetrying = true;
        try
        {
            var snapshot = await _shellSyncCenterService.GetSnapshotAsync();
            ApplySyncCenterSnapshot(snapshot);
            if (snapshot.Overview.PendingCount + snapshot.Overview.FailedCount == 0)
            {
                return;
            }

            ConsoleLog.Write("OrderSync", "auto retry pending start");
            var result = await _orderUploadExecutionService.ExecutePendingAsync(cancellationToken: cancellationToken);
            await RefreshPendingSyncAsync();
            ConsoleLog.Write(
                "OrderSync",
                $"auto retry pending completed attempted={result.AttemptedCount} uploaded={result.UploadedCount} failed={result.FailedCount}");
        }
        catch (OperationCanceledException)
        {
            ConsoleLog.Write("OrderSync", "auto retry pending canceled");
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("OrderSync", $"auto retry pending failed error={ex.GetType().Name} message={ex.Message}");
            try
            {
                await RefreshPendingSyncAsync();
            }
            catch (Exception refreshEx) when (refreshEx is not OperationCanceledException)
            {
                ConsoleLog.Write(
                    "OrderSync",
                    $"auto retry pending refresh failed error={refreshEx.GetType().Name} message={refreshEx.Message}");
            }
        }
        finally
        {
            _isAutoOrderSyncRetrying = false;
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

    private void PrewarmCustomerDisplay()
    {
        if (_customerDisplayPrewarmed)
        {
            ConsoleLog.Write(
                "CustomerDisplay",
                $"startup prewarm skipped store={Session.StoreCode} device={Session.DeviceCode} reason=already-prewarmed");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"startup prewarm start store={Session.StoreCode} device={Session.DeviceCode} currentMode={CustomerDisplayWindowMode}");
        try
        {
            _customerDisplayOrchestrator.Prewarm(CustomerDisplay, Session, _cart);
            _customerDisplayPrewarmed = true;
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"startup prewarm completed store={Session.StoreCode} device={Session.DeviceCode} currentMode={CustomerDisplayWindowMode} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"startup prewarm failed store={Session.StoreCode} device={Session.DeviceCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    private async Task BeginDeviceReregistrationFromPosAsync()
    {
        await BeginDeviceReregistrationAsync();
    }

    private async Task<DeviceReregistrationStartResult> BeginDeviceReregistrationAsync()
    {
        if (_startupOptions?.PreviewMode == true)
        {
            StatusMessage = _localization.T("main.reregister.previewUnsupported");
            return DeviceReregistrationStartResult.Blocked(StatusMessage);
        }

        if (!_cart.IsEmpty)
        {
            StatusMessage = _localization.T("main.reregister.cartNotEmpty");
            return DeviceReregistrationStartResult.Blocked(StatusMessage);
        }

        var syncSnapshot = await _shellSyncCenterService.GetSnapshotAsync();
        var overview = syncSnapshot.Overview;
        if (overview.PendingCount > 0 || overview.FailedCount > 0 || overview.SyncingCount > 0)
        {
            StatusMessage = _localization.T("main.reregister.syncPending");
            ApplySyncCenterSnapshot(syncSnapshot);
            return DeviceReregistrationStartResult.Blocked(StatusMessage);
        }

        var startupOptions = _startupOptions ?? new AppStartupOptions([], false, null, null);
        DeviceRegistration = CreateDeviceRegistrationViewModel(startupOptions);
        _pendingDeviceRegistrationCache = null;
        _deviceRegistrationStoreLoadTask = null;
        ClearCashPaymentCache();
        DeviceRegistration.PrepareReregister(Session.StoreCode);
        IsDeviceReregistrationDialogOpen = true;
        // 弹窗打开后立即加载可切换分店，避免用户首次看到空列表。
        _deviceRegistrationStoreLoadTask = DeviceRegistration.LoadStoresAsync(null);
        await _deviceRegistrationStoreLoadTask;
        if (DeviceRegistration is null || !IsDeviceReregistrationDialogOpen)
        {
            // 用户可能在门店加载完成前已经取消，后台加载结束后不再恢复或读取弹窗状态。
            return DeviceReregistrationStartResult.Blocked(StatusMessage);
        }

        return DeviceReregistrationStartResult.StartedWith(DeviceRegistration.StatusMessage);
    }

    private void ApplyDeviceReregistered()
    {
        _mainShellStartupService.ClearAuthorization();
        _posPostShowStartupTask = null;
        CancelStartupCatalogIndexLoad();
        PosTerminal?.Dispose();
        SpecialProducts?.Dispose();
        ReceiptReturns?.Dispose();
        PosTerminal = null;
        SpecialProducts = null;
        CachedPosTerminalScreen = null;
        ClearCashPaymentCache();
        CachedSpecialProductsScreen = null;
        InstallmentCenter = null;
        InstallmentCreate = null;
        ReceiptReturns = null;
        TransactionHistory = null;
        _lastCompletedOrder = null;
        _cart.Clear();
        SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Closed, Application.Current?.MainWindow);
        StatusMessage = _localization.T("main.reregister.submitted");
    }

    private void CancelDeviceReregistration()
    {
        var wasCurrentScreen = ReferenceEquals(CurrentScreen, DeviceRegistration);
        if (PosTerminal is null)
        {
            IsDeviceReregistrationDialogOpen = false;
            DeviceRegistration = null;
            return;
        }

        DeviceRegistration = null;
        IsDeviceReregistrationDialogOpen = false;
        _deviceRegistrationStoreLoadTask = null;
        if (wasCurrentScreen)
        {
            ShowPos();
        }
        StatusMessage = _localization.T("main.reregister.cancelled");
    }

    private void OnCartChanged(object? sender, EventArgs e)
    {
        CashPayment?.RefreshCart();
        InstallmentCenter?.Prepare(Session, CreateCurrentCartSnapshot());
        if (InstallmentCreate is not null)
        {
            InstallmentCreate.CartSnapshot = CreateCurrentCartSnapshot();
        }
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
        PrepareCachedSpecialProductsScreen();
        SpecialProducts.ActivateForEntry();
        CurrentScreen = SpecialProducts;
        _ = EnsureSpecialProductsLoadedAsync(SpecialProducts);
        return Task.CompletedTask;
    }

    private void ShowReturns()
    {
        if (ReceiptReturns is null)
        {
            return;
        }

        ReceiptReturns.Session = Session;
        CurrentScreen = ReceiptReturns;
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

        PrepareCachedCashPaymentScreen();
        if (CashPayment is null)
        {
            ShowPos();
            return;
        }

        CashPayment.PrepareForEntry(Session);
        CurrentScreen = CashPayment;
    }

    private async Task<bool> RecoverCardPaymentAttemptAsync(bool navigateToPaymentOnDraft)
    {
        if (_cardPaymentRecoveryService is null)
        {
            return false;
        }

        var recoveryTask = _cardPaymentRecoveryTask;
        if (recoveryTask is null)
        {
            recoveryTask = _cardPaymentRecoveryService.RecoverLatestAsync(_cart, Session, CancellationToken.None);
            _cardPaymentRecoveryTask = recoveryTask;
        }

        CardPaymentRecoveryResult result;
        try
        {
            result = await recoveryTask;
        }
        catch
        {
            if (ReferenceEquals(_cardPaymentRecoveryTask, recoveryTask))
            {
                _cardPaymentRecoveryTask = null;
            }

            throw;
        }

        if (ShouldRetryCardPaymentRecovery(result.Outcome) &&
            ReferenceEquals(_cardPaymentRecoveryTask, recoveryTask))
        {
            _cardPaymentRecoveryTask = null;
        }

        if (result.Outcome == CardPaymentRecoveryOutcome.None)
        {
            return false;
        }

        StatusMessage = result.Message;
        if (result.UpdatedSession is not null)
        {
            Session = result.UpdatedSession;
        }

        if (result.Outcome == CardPaymentRecoveryOutcome.OrderCompleted && result.Order is not null)
        {
            _lastCompletedOrder = result.Order;
            await RefreshPendingSyncAsync();
            await PaymentSuccess.LoadFromOrderAsync(result.Order);
            CurrentScreen = PaymentSuccess;
            LogRecoveredCardOrderCompleted(result.Order);
            var printResult = await PrintRecoveredCardReceiptAsync(result.Order);
            await ShowRecoveredCardOrderDialogAsync(result, printResult);
            ShowCashPaymentCommand.NotifyCanExecuteChanged();
            return true;
        }

        if (result.Outcome == CardPaymentRecoveryOutcome.DraftRestored)
        {
            PosTerminal?.RefreshCart();
            CashPayment?.RefreshCart();
            ShowCashPaymentCommand.NotifyCanExecuteChanged();
            if (navigateToPaymentOnDraft && !_cart.IsEmpty)
            {
                PrepareCachedCashPaymentScreen();
                CashPayment?.PrepareForEntry(Session);
                CurrentScreen = CashPayment;
            }

            ShowRecoveredCardDraftDialog(result);
            return true;
        }

        if (result.Outcome == CardPaymentRecoveryOutcome.Unknown)
        {
            ShowRecoveredCardFailureDialog(result);
        }

        return false;
    }

    private async Task RecoverActiveCardPaymentSessionFromPaymentAsync()
    {
        if (_cardPaymentRecoveryService is null)
        {
            return;
        }

        var result = await _cardPaymentRecoveryService.RecoverActiveSessionAsync(_cart, Session, CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            StatusMessage = result.Message;
        }

        if (result.UpdatedSession is not null)
        {
            Session = result.UpdatedSession;
        }

        if (result.Outcome == CardPaymentRecoveryOutcome.None)
        {
            return;
        }

        if (result.Outcome == CardPaymentRecoveryOutcome.DraftRestored)
        {
            // 付款页主动恢复的是旧 active session，不能把恢复结果自动混入当前购物车。
            ShowRecoveredCardDraftDialog(result);
            return;
        }

        if (result.Outcome is CardPaymentRecoveryOutcome.Unknown or CardPaymentRecoveryOutcome.Checking)
        {
            ShowRecoveredCardFailureDialog(result);
        }
    }

    private async Task<ReceiptPrintResult> PrintRecoveredCardReceiptAsync(LocalOrder order)
    {
        var evidence = GetCardRecoveryEvidence(order);
        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "power-fail-recovery-print",
            "request",
            direction: "request",
            sessionId: evidence.SessionId,
            request: new
            {
                reason = ReceiptPrintReason.CardAuto.ToString(),
                orderGuid = order.OrderGuid
            },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                certCase = "4.1.3",
                orderGuid = order.OrderGuid,
                transactionReference = evidence.TransactionReference,
                evidence.TxnRef,
                evidence.SessionId,
                reason = "4.1.3"
            });

        var printResult = await PrintReceiptAsync(ReceiptQueryService.CreateReceipt(order), ReceiptPrintReason.CardAuto);
        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "power-fail-recovery-print",
            "response",
            direction: "response",
            sessionId: evidence.SessionId,
            success: printResult.Succeeded,
            reason: printResult.Succeeded ? null : "receipt-print-failed",
            response: new
            {
                printResult.Succeeded,
                printResult.Message,
                printResult.OrderGuid
            },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                certCase = "4.1.3",
                orderGuid = order.OrderGuid,
                transactionReference = evidence.TransactionReference,
                evidence.TxnRef,
                evidence.SessionId,
                reason = "4.1.3"
            });
        return printResult;
    }

    private async Task ShowRecoveredCardOrderDialogAsync(
        CardPaymentRecoveryResult result,
        ReceiptPrintResult printResult)
    {
        if (result.Order is null)
        {
            return;
        }

        var receipt = ReceiptQueryService.CreateReceipt(result.Order);
        _cardRecoveryDialogReceipt = receipt;
        var previewRows = await BuildReceiptPreviewRowsAsync(receipt);
        var details = result.DialogDetails;
        var printMessage = printResult.Succeeded
            ? _localization.T("cardRecovery.dialog.message.autoPrintSucceeded")
            : string.Format(
                _localization.CurrentCulture,
                _localization.T("cardRecovery.dialog.message.autoPrintFailed"),
                printResult.Message);

        ShowCardRecoveryResultDialog(new CardRecoveryResultDialogViewModel(
            _localization.T("cardRecovery.dialog.title.completed"),
            printMessage,
            printResult.Succeeded ? CardRecoveryResultSeverity.Success : CardRecoveryResultSeverity.Warning,
            result.Order.OrderGuid,
            result.Order.ActualAmount,
            details?.SessionId ?? GetCardRecoveryEvidence(result.Order).SessionId,
            details?.TxnRef ?? GetCardRecoveryEvidence(result.Order).TxnRef,
            details?.ResponseCode ?? GetCardRecoveryResponseCode(result.Order),
            details?.ResponseText ?? GetCardRecoveryResponseText(result.Order),
            details?.Timestamp ?? DateTimeOffset.Now,
            previewRows,
            canPrintReceipt: true,
            printButtonText: _localization.T("cardRecovery.dialog.action.printReceipt")));
    }

    private void ShowRecoveredCardDraftDialog(CardPaymentRecoveryResult result)
    {
        var details = result.DialogDetails;
        ShowCardRecoveryResultDialog(new CardRecoveryResultDialogViewModel(
            _localization.T("cardRecovery.dialog.title.draftRestored"),
            string.IsNullOrWhiteSpace(result.Message)
                ? _localization.T("cardRecovery.dialog.message.draftRestoredFallback")
                : result.Message,
            CardRecoveryResultSeverity.Warning,
            orderGuid: null,
            amount: details?.Amount,
            sessionId: details?.SessionId,
            txnRef: details?.TxnRef,
            responseCode: details?.ResponseCode,
            responseText: details?.ResponseText,
            timestamp: details?.Timestamp ?? DateTimeOffset.Now));
    }

    private void ShowRecoveredCardFailureDialog(CardPaymentRecoveryResult result)
    {
        var details = result.DialogDetails;
        ShowCardRecoveryResultDialog(new CardRecoveryResultDialogViewModel(
            _localization.T("cardRecovery.dialog.title.failed"),
            string.IsNullOrWhiteSpace(result.Message)
                ? _localization.T("cardRecovery.dialog.message.failedFallback")
                : result.Message,
            CardRecoveryResultSeverity.Error,
            orderGuid: null,
            amount: details?.Amount,
            sessionId: details?.SessionId,
            txnRef: details?.TxnRef,
            responseCode: details?.ResponseCode,
            responseText: details?.ResponseText,
            timestamp: details?.Timestamp ?? DateTimeOffset.Now));
    }

    private void OnCardRecoveryResultDialogRequested(object? sender, CardRecoveryResultDialogViewModel dialog)
    {
        ShowCardRecoveryResultDialog(dialog);
    }

    private void ShowCardRecoveryResultDialog(CardRecoveryResultDialogViewModel dialog)
    {
        CardRecoveryResultDialog = dialog;
        IsCardRecoveryResultDialogOpen = true;
        PrintRecoveredReceiptCommand.NotifyCanExecuteChanged();
    }

    private void CloseCardRecoveryResultDialog()
    {
        IsCardRecoveryResultDialogOpen = false;
        CardRecoveryResultDialog = null;
        _cardRecoveryDialogReceipt = null;
        PrintRecoveredReceiptCommand.NotifyCanExecuteChanged();
    }

    private bool CanPrintRecoveredReceipt()
    {
        return CardRecoveryResultDialog?.CanPrintReceipt == true &&
            _cardRecoveryDialogReceipt is not null;
    }

    private async Task PrintRecoveredReceiptAsync()
    {
        if (_cardRecoveryDialogReceipt is null)
        {
            return;
        }

        await PrintReceiptAsync(_cardRecoveryDialogReceipt, ReceiptPrintReason.CardAuto);
    }

    private async Task<IReadOnlyList<ReceiptPreviewRow>> BuildReceiptPreviewRowsAsync(ReceiptDetails receipt)
    {
        var settings = ReceiptPrinterSettings.Default;
        if (_receiptPrinterSettingsStore is not null)
        {
            try
            {
                settings = await _receiptPrinterSettingsStore.LoadAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                settings = ReceiptPrinterSettings.Default;
            }
        }

        try
        {
            return _receiptTextFormatter.Build(receipt, settings, receipt.SoldAt).PreviewRows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    private static string? GetCardRecoveryResponseCode(LocalOrder order)
    {
        return order.Payments
            .FirstOrDefault(payment => payment.Method == PaymentMethodKind.Card)?
            .CardTransactions?
            .FirstOrDefault()?
            .ResponseCode;
    }

    private static string? GetCardRecoveryResponseText(LocalOrder order)
    {
        return order.Payments
            .FirstOrDefault(payment => payment.Method == PaymentMethodKind.Card)?
            .CardTransactions?
            .FirstOrDefault()?
            .ResponseText;
    }

    private static void LogRecoveredCardOrderCompleted(LocalOrder order)
    {
        var evidence = GetCardRecoveryEvidence(order);
        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "power-fail-recovery",
            "order-completed",
            sessionId: evidence.SessionId,
            success: true,
            details: new
            {
                timestamp = DateTimeOffset.Now,
                certCase = "4.1.2",
                orderGuid = order.OrderGuid,
                transactionReference = evidence.TransactionReference,
                evidence.TxnRef,
                evidence.SessionId,
                reason = "4.1.2"
            });
    }

    private static CardRecoveryEvidence GetCardRecoveryEvidence(LocalOrder order)
    {
        var cardPayment = order.Payments.FirstOrDefault(payment => payment.Method == PaymentMethodKind.Card);
        var cardTransaction = cardPayment?.CardTransactions?.FirstOrDefault();
        var txnRef = NormalizeEvidenceValue(cardTransaction?.TxnRef) ?? TryReadLinklyBackendTxnRef(cardPayment?.Reference);
        var sessionId = LinklyBackendPaymentReference.TryGetPrintMarker(cardPayment?.Reference, out _, out var markerSessionId)
            ? NormalizeEvidenceValue(markerSessionId)
            : null;
        return new CardRecoveryEvidence(
            NormalizeEvidenceValue(sessionId) ?? NormalizeEvidenceValue(txnRef) ?? order.OrderGuid.ToString("D"),
            NormalizeEvidenceValue(txnRef),
            NormalizeEvidenceValue(sessionId));
    }

    private static string? TryReadLinklyBackendTxnRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) ||
            !reference.StartsWith($"{LinklyBackendPaymentReference.Prefix}:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = reference.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? NormalizeEvidenceValue(parts[1]) : null;
    }

    private static string? NormalizeEvidenceValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record CardRecoveryEvidence(string TransactionReference, string? TxnRef, string? SessionId);

    private static bool ShouldRetryCardPaymentRecovery(CardPaymentRecoveryOutcome outcome)
    {
        return outcome is CardPaymentRecoveryOutcome.None or
            CardPaymentRecoveryOutcome.Checking or
            CardPaymentRecoveryOutcome.Unknown;
    }

    private void ShowInstallmentCenter()
    {
        if (CashPayment is null)
        {
            return;
        }

        // 分期入口从支付页进入，先带上当前购物车汇总做 UI 骨架展示。
        InstallmentCenter ??= CreateInstallmentCenterViewModel();
        InstallmentCenter.Prepare(Session, CreateCurrentCartSnapshot());
        CurrentScreen = InstallmentCenter;
        _ = InstallmentCenter.LoadAsync();
    }

    private async Task ShowInstallmentCreateAsync(PosCartServiceSnapshot? cartSnapshot)
    {
        InstallmentCreate ??= CreateInstallmentCreateViewModel();
        InstallmentCreate.Prepare(Session, cartSnapshot);
        CurrentScreen = InstallmentCreate;
        await Task.CompletedTask;
    }

    private void ClearCashPaymentCache()
    {
        if (CashPayment is not null)
        {
            CashPayment.PropertyChanged -= OnCashPaymentPropertyChanged;
        }

        CachedCashPaymentScreen = null;
        CashPayment = null;
    }

    private void OnCashPaymentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaymentViewModel.PaymentMode) &&
            ReferenceEquals(CurrentScreen, CashPayment))
        {
            OnPropertyChanged(nameof(ActivePageTitleText));
        }
    }

    private async void OnPaymentCompleted(object? sender, PaymentCompletedEventArgs e)
    {
        _lastCompletedOrder = e.Order;
        await RefreshPendingSyncAsync();
        await PaymentSuccess.LoadFromOrderAsync(e.Order);
        CurrentScreen = PaymentSuccess;

        ShowCashPaymentCommand.NotifyCanExecuteChanged();
        if (ContainsCashPayment(e.Order))
        {
            var cashDrawerResult = await OpenCashDrawerAsync();
            if (!cashDrawerResult.Succeeded)
            {
                StatusMessage = cashDrawerResult.Message;
            }
        }

        if (ContainsCardPayment(e.Order))
        {
            await PrintReceiptAsync(ReceiptQueryService.CreateReceipt(e.Order), ReceiptPrintReason.CardAuto);
        }
    }

    private async Task ShowPaymentSuccessLatestAsync()
    {
        if (_lastCompletedOrder is not null)
        {
            await PaymentSuccess.LoadFromOrderAsync(_lastCompletedOrder);
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

    private async Task ShowDailyCloseAsync()
    {
        DailyClose ??= new DailyCloseViewModel(
            _dailyCloseService,
            _dailyClosePrintService,
            Session,
            _localization,
            ShowPos);
        DailyClose.Session = Session;
        await DailyClose.LoadAsync();
        CurrentScreen = DailyClose;
    }

    private async Task ShowSettingsAsync()
    {
        if (_cardTerminalSetupService is null)
        {
            StatusMessage = _localization.T("main.settingsUnavailable");
            return;
        }

        Func<CancellationToken, Task>? resetTestSalesDataAsync = null;
        Func<bool>? confirmResetTestSalesData = null;
#if DEBUG
        resetTestSalesDataAsync = async cancellationToken =>
        {
            if (_testSalesDataResetService is null)
            {
                throw new InvalidOperationException(_localization.T("settings.status.testSalesDataResetNotConfigured"));
            }

            await _testSalesDataResetService.ResetAsync(cancellationToken);
        };
        confirmResetTestSalesData = _confirmationDialogService.ConfirmResetTestSalesData;
#endif

        Settings ??= new SettingsViewModel(
            _cardTerminalSetupService,
            _localization,
            async cancellationToken =>
            {
                await SyncCatalogAndReloadAsync(cancellationToken);
            },
            async cancellationToken =>
            {
                await ResetCatalogAndReloadAsync(cancellationToken);
            },
            BeginDeviceReregistrationAsync,
            ShowPos,
            _receiptPrinterSettingsStore,
            _receiptPrintService,
            resetTestSalesDataAsync: resetTestSalesDataAsync,
            confirmResetTestSalesData: confirmResetTestSalesData,
            cardRecoveryResultDialogService: _cardRecoveryResultDialogService);
        await Settings.LoadAsync();
        CurrentScreen = Settings;
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
            StatusMessage = _localization.T("main.suspendedUnavailable");
            return;
        }

        try
        {
            var suspended = await _suspendedOrderService.SuspendCurrentOrderAsync(Session);
            PosTerminal?.RefreshCart();
            CashPayment?.RefreshCart();
            StatusMessage = string.Format(_localization.CurrentCulture, _localization.T("main.suspendedSaved"), suspended.SuspendedOrderGuid.ToString("N")[..8].ToUpperInvariant());
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private TransactionHistoryViewModel CreateTransactionHistoryViewModel()
    {
        var viewModel = new TransactionHistoryViewModel(
            _receiptQueryService,
            _suspendedOrderService,
            _remoteOrderHistoryService,
            Session,
            OnSuspendedOrderRecalledAsync,
            ShowPos,
            _localization,
            _receiptTextFormatter,
            _receiptPrinterSettingsStore);
        viewModel.ReprintRequested += async (_, _) => await PrintSelectedHistoryReceiptAsync(viewModel);
        return viewModel;
    }

    private InstallmentCenterViewModel CreateInstallmentCenterViewModel()
    {
        return new InstallmentCenterViewModel(
            _installmentOrderService,
            Session,
            ShowInstallmentCreateAsync,
            ShowCashPayment,
            _localization,
            _cardTerminalClient);
    }

    private InstallmentCreateViewModel CreateInstallmentCreateViewModel()
    {
        return new InstallmentCreateViewModel(
            _installmentOrderService,
            Session,
            async order =>
            {
                InstallmentCenter ??= CreateInstallmentCenterViewModel();
                InstallmentCenter.Prepare(Session, CreateCurrentCartSnapshot());
                InstallmentCenter.AppendOrUpdateOrder(order);
                CurrentScreen = InstallmentCenter;
                await InstallmentCenter.LoadAsync();
            },
            () =>
            {
                InstallmentCenter ??= CreateInstallmentCenterViewModel();
                InstallmentCenter.Prepare(Session, CreateCurrentCartSnapshot());
                CurrentScreen = InstallmentCenter;
            },
            _localization);
    }

    private PosCartServiceSnapshot? CreateCurrentCartSnapshot()
    {
        if (_cart.IsEmpty)
        {
            return null;
        }

        return new PosCartServiceSnapshot(
            _cart.TotalAmount,
            _cart.DiscountAmount,
            _cart.ActualAmount,
            _cart.Lines.Select(line => new PosCartLineServiceSnapshot(
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.ItemNumber,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.ActualAmount)).ToList());
    }

    private async Task<ReceiptPrintResult> PrintLatestReceiptAsync()
    {
        ReceiptPrintResult result;
        try
        {
            result = await _receiptPrintService.PrintLatestReceiptAsync(ReceiptPrintReason.LastReceipt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new ReceiptPrintResult(false, ex.Message);
        }

        ApplyReceiptPrintStatus(result);
        return result;
    }

    private async Task<ReceiptPrintResult> OpenCashDrawerAsync()
    {
        try
        {
            return await _cashDrawerService.OpenAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReceiptPrintResult(false, ex.Message);
        }
    }

    private Task ExitApplicationAsync()
    {
        if (!_confirmationDialogService.ConfirmExitApplication())
        {
            return Task.CompletedTask;
        }

        SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Closed, Application.Current?.MainWindow);
        _applicationExitService.Exit();
        return Task.CompletedTask;
    }

    private async Task PrintPaymentSuccessReceiptAsync()
    {
        if (PaymentSuccess.TransactionId is not Guid orderGuid)
        {
            return;
        }

        await PrintReceiptAsync(orderGuid, ReceiptPrintReason.Manual);
    }

    private async Task PrintSelectedHistoryReceiptAsync(TransactionHistoryViewModel history)
    {
        if (history.SelectedOrder is null)
        {
            return;
        }

        await PrintReceiptAsync(history.SelectedOrder.OrderGuid, ReceiptPrintReason.Reprint);
    }

    private async Task<ReceiptPrintResult> PrintReceiptAsync(Guid orderGuid, ReceiptPrintReason reason)
    {
        ReceiptPrintResult result;
        try
        {
            result = await _receiptPrintService.PrintReceiptAsync(orderGuid, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new ReceiptPrintResult(false, ex.Message, orderGuid);
        }

        ApplyReceiptPrintStatus(result);
        return result;
    }

    private async Task<ReceiptPrintResult> PrintReceiptAsync(ReceiptDetails receipt, ReceiptPrintReason reason)
    {
        ReceiptPrintResult result;
        try
        {
            result = await _receiptPrintService.PrintReceiptAsync(receipt, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new ReceiptPrintResult(false, ex.Message, receipt.OrderGuid);
        }

        ApplyReceiptPrintStatus(result);
        return result;
    }

    private void ApplyReceiptPrintStatus(ReceiptPrintResult result)
    {
        StatusMessage = result.Succeeded
            ? _localization.T("receipt.print.success")
            : string.Format(
                _localization.CurrentCulture,
                _localization.T("receipt.print.failed"),
                result.Message);
    }

    private static bool ContainsCardPayment(LocalOrder order)
    {
        return order.Payments.Any(payment => payment.Method == PaymentMethodKind.Card);
    }

    private static bool ContainsCashPayment(LocalOrder order)
    {
        return order.Payments.Any(payment => payment.Method == PaymentMethodKind.Cash);
    }

    private Task OnSuspendedOrderRecalledAsync()
    {
        PosTerminal?.RefreshCart();
        CashPayment?.RefreshCart();
        ShowPos();
        StatusMessage = _localization.T("main.suspendedRecalled");
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

    private async Task RetrySyncOrderAsync(SyncQueueListItem? item)
    {
        if (item is null)
        {
            return;
        }

        await ExecuteOrderSyncRetryAsync(
            () => _orderUploadExecutionService.ExecuteOneAsync(item.EntityId),
            "shell.sync.retryingOne");
    }

    private async Task RetryAllSyncOrdersAsync()
    {
        await ExecuteOrderSyncRetryAsync(
            () => _orderUploadExecutionService.ExecutePendingAsync(),
            "shell.sync.retryingAll");
    }

    private async Task ExecuteOrderSyncRetryAsync(
        Func<Task<OrderUploadExecutionResult>> executeAsync,
        string retryingStatusKey)
    {
        if (IsOrderSyncRetrying)
        {
            return;
        }

        IsOrderSyncRetrying = true;
        StatusMessage = _localization.T(retryingStatusKey);
        try
        {
            var result = await executeAsync();
            await RefreshPendingSyncAsync();
            StatusMessage = string.Format(
                _localization.CurrentCulture,
                _localization.T("shell.sync.retryCompleted"),
                result.UploadedCount,
                result.FailedCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RefreshPendingSyncAsync();
            StatusMessage = string.Format(
                _localization.CurrentCulture,
                _localization.T("shell.sync.retryFailed"),
                ex.Message);
        }
        finally
        {
            IsOrderSyncRetrying = false;
        }
    }

    private bool CanRetrySyncOrder(SyncQueueListItem? item)
    {
        return !IsOrderSyncRetrying &&
            item is not null &&
            item.EntityType.Equals("Order", StringComparison.OrdinalIgnoreCase) &&
            IsRetryableSyncStatus(item.Status);
    }

    private bool CanRetryAllSyncOrders()
    {
        return !IsOrderSyncRetrying && PendingUploadCount + FailedUploadCount > 0;
    }

    private static bool IsRetryableSyncStatus(string status)
    {
        return status.Equals("Pending", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("Failed", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshSyncRetryCommandStates()
    {
        RetrySyncOrderCommand.NotifyCanExecuteChanged();
        RetryAllSyncOrdersCommand.NotifyCanExecuteChanged();
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
        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"viewmodel set-mode start requestedMode={mode} currentMode={CustomerDisplayWindowMode} ownerPresent={owner is not null} store={Session.StoreCode} device={Session.DeviceCode}");
        var result = _customerDisplayOrchestrator.SetMode(mode, CustomerDisplay, Session, _cart, owner);
        ApplyCustomerDisplayWindowResult(result);
        stopwatch.Stop();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"viewmodel set-mode completed requestedMode={mode} resultMode={result.Mode} open={IsCustomerDisplayOpen} elapsedMs={stopwatch.ElapsedMilliseconds}");
    }

    private void OpenCustomerDisplayWindow(Window? owner)
    {
        ConsoleLog.Write(
            "CustomerDisplay",
            $"startup open-window request store={Session.StoreCode} device={Session.DeviceCode} ownerPresent={owner is not null}");
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
        StatusMessage = _localization.T("main.scannerBindingReset");
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
            case "settings":
                _ = ShowSettingsAsync();
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
