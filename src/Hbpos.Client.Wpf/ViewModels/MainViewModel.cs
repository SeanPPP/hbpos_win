using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const string LanguageSettingKey = "Language";

    private readonly LocalSellableItemIndex _priceIndex;
    private readonly PosCartService _cart;
    private readonly CashCheckoutService _checkout;
    private readonly ILocalSchemaService _schema;
    private readonly ILocalAppSettingsRepository _settingsRepository;
    private readonly ILocalCatalogRepository _catalogRepository;
    private readonly ILocalCatalogSyncService _catalogSync;
    private readonly IRemoteLookupRefreshService _remoteLookupRefresh;
    private readonly ILocalOrderRepository _orderRepository;
    private readonly ISyncQueueRepository _syncQueueRepository;
    private readonly ILocalizationService _localization;
    private readonly ICustomerDisplayWindowService _customerDisplayWindowService;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private bool _isApplyingCulture;
    private bool _schemaReady;
    private LocalOrder? _lastCompletedOrder;

    [ObservableProperty]
    private PosSessionState _session = new("HB POS", "S001", "Main Branch", "Terminal 04", "C001", "Alice", false, 0);

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
    private int _pendingUploadCount;

    [ObservableProperty]
    private int _failedUploadCount;

    [ObservableProperty]
    private int _syncingOrderCount;

    public MainViewModel(
        LocalSellableItemIndex priceIndex,
        PosCartService cart,
        CashCheckoutService checkout,
        ILocalSchemaService schema,
        ILocalAppSettingsRepository settingsRepository,
        ILocalCatalogRepository catalogRepository,
        ILocalCatalogSyncService catalogSync,
        IRemoteLookupRefreshService remoteLookupRefresh,
        ILocalOrderRepository orderRepository,
        ISyncQueueRepository syncQueueRepository,
        ILocalizationService localization,
        ICustomerDisplayWindowService customerDisplayWindowService)
    {
        _priceIndex = priceIndex;
        _cart = cart;
        _checkout = checkout;
        _schema = schema;
        _settingsRepository = settingsRepository;
        _catalogRepository = catalogRepository;
        _catalogSync = catalogSync;
        _remoteLookupRefresh = remoteLookupRefresh;
        _orderRepository = orderRepository;
        _syncQueueRepository = syncQueueRepository;
        _localization = localization;
        _customerDisplayWindowService = customerDisplayWindowService;

        PaymentSuccess = new PaymentSuccessViewModel(_orderRepository);

        ShowPosCommand = new RelayCommand(ShowPos);
        ShowCashPaymentCommand = new RelayCommand(ShowCashPayment, () => !_cart.IsEmpty);
        ShowPaymentSuccessCommand = new AsyncRelayCommand(ShowPaymentSuccessLatestAsync);
        ShowHistoryCommand = new AsyncRelayCommand(ShowHistoryAsync);
        ShowCustomerDisplayCommand = new RelayCommand(ShowCustomerDisplay);
        ToggleSyncCenterCommand = new AsyncRelayCommand(ToggleSyncCenterAsync);
        ToggleCustomerDisplayWindowCommand = new RelayCommand(ToggleCustomerDisplayWindow);

        _cart.CartChanged += OnCartChanged;
        _localization.CultureChanged += OnCultureChanged;
        _customerDisplayWindowService.Closed += (_, _) => IsCustomerDisplayOpen = false;
        _clockTimer.Tick += (_, _) => RefreshClock();
        RefreshLocalizedShell(resetStatus: true);
    }

    public PosTerminalViewModel? PosTerminal { get; private set; }

    public CashPaymentViewModel? CashPayment { get; private set; }

    public PaymentSuccessViewModel PaymentSuccess { get; }

    public TransactionHistoryViewModel? TransactionHistory { get; private set; }

    public CustomerDisplayViewModel CustomerDisplay { get; } = new();

    public ObservableCollection<SyncQueueListItem> SyncCenterOrders { get; } = [];

    public IRelayCommand ShowPosCommand { get; }

    public IRelayCommand ShowCashPaymentCommand { get; }

    public IAsyncRelayCommand ShowPaymentSuccessCommand { get; }

    public IAsyncRelayCommand ShowHistoryCommand { get; }

    public IRelayCommand ShowCustomerDisplayCommand { get; }

    public IAsyncRelayCommand ToggleSyncCenterCommand { get; }

    public IRelayCommand ToggleCustomerDisplayWindowCommand { get; }

    public async Task InitializeAsync(AppStartupOptions startupOptions)
    {
        await _schema.InitializeAsync();
        _schemaReady = true;

        await RestoreLanguageAsync(startupOptions);

        IReadOnlyList<SellableItemDto> cachedItems;
        if (startupOptions.PreviewMode)
        {
            cachedItems = CreateStarterItems();
            await _catalogRepository.ReplaceSellableItemsAsync(cachedItems);
            _priceIndex.ReplaceAll(cachedItems);
        }
        else
        {
            cachedItems = await ReloadCatalogIndexAsync();
        }

        PosTerminal = new PosTerminalViewModel(
            _priceIndex,
            _cart,
            Session,
            ShowCashPayment,
            _localization,
            RefreshRemoteLookupAsync,
            ReloadCatalogIndexAsync,
            SyncCatalogAndReloadAsync);
        PosTerminal.LoadMatches(cachedItems);

        TransactionHistory = new TransactionHistoryViewModel(_orderRepository);
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

        if (!startupOptions.PreviewMode)
        {
            BeginInitialCatalogSync();
        }
    }

    partial void OnSessionChanged(PosSessionState value)
    {
        RefreshLocalizedShell();
        ApplySessionToScreens();
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
        if (startupOptions.PreviewMode)
        {
            await ApplyLanguageAsync(startupOptions.InitialCulture ?? LocalizationService.DefaultCultureName, persist: false);
            return;
        }

        var cultureName = startupOptions.InitialCulture;
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            cultureName = await _settingsRepository.GetValueAsync(LanguageSettingKey) ?? LocalizationService.DefaultCultureName;
        }

        await ApplyLanguageAsync(cultureName, persist: startupOptions.InitialCulture is not null);
    }

    private async Task ApplyLanguageAsync(string cultureName, bool persist)
    {
        try
        {
            _localization.SetCulture(cultureName);
        }
        catch (ArgumentException)
        {
            cultureName = LocalizationService.DefaultCultureName;
            _localization.SetCulture(cultureName);
        }

        _isApplyingCulture = true;
        SelectedCultureName = _localization.CurrentCulture.Name;
        _isApplyingCulture = false;

        if (persist && _schemaReady)
        {
            await _settingsRepository.SetValueAsync(LanguageSettingKey, _localization.CurrentCulture.Name);
        }
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
        var overview = await _syncQueueRepository.GetOverviewAsync();
        PendingUploadCount = overview.PendingCount;
        FailedUploadCount = overview.FailedCount;
        SyncingOrderCount = overview.SyncingCount;
        LastOrderSyncErrorText = overview.LastError ?? _localization.T("shell.sync.noErrors");

        var activeItems = await _syncQueueRepository.GetActiveItemsAsync();
        SyncCenterOrders.ReplaceWith(activeItems);

        Session = Session with { PendingSyncCount = overview.PendingCount };
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
    }

    private void BeginInitialCatalogSync()
    {
        if (!Session.IsOnline)
        {
            return;
        }

        _ = TryInitialCatalogSyncAsync();
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

    private async Task<IReadOnlyList<SellableItemDto>> SyncCatalogAndReloadAsync(CancellationToken cancellationToken)
    {
        await _catalogSync.FullSyncAsync(Session.StoreCode, cancellationToken);
        return await ReloadCatalogIndexAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SellableItemDto>> ReloadCatalogIndexAsync(CancellationToken cancellationToken = default)
    {
        var cachedItems = await _catalogRepository.LoadSellableItemsAsync(cancellationToken);
        _priceIndex.ReplaceAll(cachedItems);
        return cachedItems;
    }

    private void OnCartChanged(object? sender, EventArgs e)
    {
        PosTerminal?.RefreshCart();
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

        PosTerminal.RefreshCart();
        CurrentScreen = PosTerminal;
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
            _checkout,
            _orderRepository,
            _syncQueueRepository,
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

        if (PosTerminal is not null)
        {
            PosTerminal.RefreshCart();
        }

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
        TransactionHistory ??= new TransactionHistoryViewModel(_orderRepository);
        await TransactionHistory.LoadAsync();
        CurrentScreen = TransactionHistory;
    }

    private void ShowCustomerDisplay()
    {
        LoadCustomerDisplayFromCart();
        CurrentScreen = CustomerDisplay;
    }

    private void LoadCustomerDisplayFromCart()
    {
        var lines = _cart.Lines.Select(line => new CustomerDisplayLine(
            line.DisplayName,
            line.LookupCode,
            line.Quantity,
            line.UnitPrice,
            line.ActualAmount));
        CustomerDisplay.TerminalName = Session.DeviceCode;
        CustomerDisplay.LoadLines(lines, _cart.TotalAmount, 0m, _cart.DiscountAmount);
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
        LoadCustomerDisplayFromCart();
        var owner = Application.Current.MainWindow;
        if (owner is null)
        {
            return;
        }

        _customerDisplayWindowService.Toggle(CustomerDisplay, owner);
        IsCustomerDisplayOpen = _customerDisplayWindowService.IsOpen;
    }

    private void ResetForNewTransaction()
    {
        _cart.Clear();
        PosTerminal?.RefreshCart();
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

        PosTerminal?.RefreshCart();
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
            new("S001", "SKU-001", null, "Organic Fuji Apple", "690001", "SKU-001", "690001", 4.50m, PriceSourceKind.StoreRetailPrice, "Store Price", 1m, now),
            new("S001", "SKU-002", null, "Whole Milk 1L", "690002", "SKU-002", "690002", 3.20m, PriceSourceKind.ProductBase, "Base Price", 1m, now),
            new("S001", "SKU-003", "SET-003", "Greek Yogurt Blueberry", "690003", "SKU-003", "690003", 3.75m, PriceSourceKind.StoreMultiCodeProduct, "Multi-code Store Price", 1m, now),
            new("S001", "SKU-004", "CLR-004", "Cold Brew Concentrate", "690004", "SKU-004", "690004", 12.90m, PriceSourceKind.StoreClearancePrice, "Clearance Price", 1m, now)
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
