using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class PosTerminalViewModel : ObservableObject
{
    private readonly LocalSellableItemIndex _priceIndex;
    private readonly PosCartService _cart;
    private readonly Action? _onOpenPayment;
    private readonly ILocalizationService? _localization;
    private readonly Func<string, string, CancellationToken, Task<RemoteLookupRefreshResult>>? _remoteLookupRefreshAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? _reloadCatalogAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? _syncCatalogAsync;
    private string _statusKey = "pos.status.ready";
    private object[] _statusArgs = [];
    private string? _statusText;

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private string _scanText = string.Empty;

    [ObservableProperty]
    private SellableItemDto? _selectedItem;

    [ObservableProperty]
    private bool _isMatchesPopupOpen;

    [ObservableProperty]
    private bool _isTouchKeyboardOpen;

    public PosTerminalViewModel(
        LocalSellableItemIndex priceIndex,
        PosCartService cart,
        PosSessionState session,
        Action? onOpenPayment,
        ILocalizationService? localization = null,
        Func<string, string, CancellationToken, Task<RemoteLookupRefreshResult>>? remoteLookupRefreshAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? reloadCatalogAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? syncCatalogAsync = null)
    {
        _priceIndex = priceIndex;
        _cart = cart;
        _session = session;
        _onOpenPayment = onOpenPayment;
        _localization = localization;
        _remoteLookupRefreshAsync = remoteLookupRefreshAsync;
        _reloadCatalogAsync = reloadCatalogAsync;
        _syncCatalogAsync = syncCatalogAsync;
        if (_localization is not null)
        {
            _localization.CultureChanged += (_, _) => RaiseLocalizedProperties();
        }

        ScanCommand = new RelayCommand(SearchAndAdd);
        NumberInputCommand = new RelayCommand<string>(AppendScanText);
        ToggleTouchKeyboardCommand = new RelayCommand(ToggleTouchKeyboard);
        AddSelectedCommand = new RelayCommand(AddSelected, () => SelectedItem is not null);
        SelectMatchCommand = new RelayCommand<SellableItemDto>(SelectMatch);
        ClearCartCommand = new RelayCommand(ClearCart, () => !_cart.IsEmpty);
        OpenPaymentCommand = new RelayCommand(OpenPayment, () => !_cart.IsEmpty);
        SyncCommand = new AsyncRelayCommand(SyncAsync);
    }

    public ObservableCollection<SellableItemDto> Matches { get; } = [];

    public ObservableCollection<CartLine> CartLines { get; } = [];

    public IRelayCommand ScanCommand { get; }

    public IRelayCommand<string> NumberInputCommand { get; }

    public IRelayCommand ToggleTouchKeyboardCommand { get; }

    public IRelayCommand AddSelectedCommand { get; }

    public IRelayCommand<SellableItemDto> SelectMatchCommand { get; }

    public IRelayCommand ClearCartCommand { get; }

    public IRelayCommand OpenPaymentCommand { get; }

    public IAsyncRelayCommand SyncCommand { get; }

    public event EventHandler? PaymentRequested;

    public string ScreenTitleText => T("pos.terminal.title");

    public string SearchPlaceholderText => T("pos.terminal.search.placeholder");

    public string SearchButtonText => T("pos.terminal.search.action");

    public string AddSelectedText => T("pos.terminal.addSelected");

    public string CartTitleText => T("pos.terminal.cart.title");

    public string TotalsLabelText => T("pos.terminal.totals.total");

    public string PayNowText => T("pos.terminal.payNow");

    public string ClearCartText => T("pos.terminal.actions.clearCart");

    public string HoldOrderText => T("pos.terminal.actions.holdOrder");

    public string RecallOrderText => T("pos.terminal.actions.recallOrder");

    public string MemberText => T("pos.terminal.actions.member");

    public string SyncText => T("pos.terminal.actions.sync");

    public string OnlineText => T(Session.IsOnline ? "pos.status.online" : "pos.status.offline");

    public string PendingSyncText => Format("pos.status.pendingSync", Session.PendingSyncCount);

    public string StatusMessage => _statusText ?? Format(_statusKey, _statusArgs);

    public decimal TotalAmount => _cart.TotalAmount;

    public decimal DiscountAmount => _cart.DiscountAmount;

    public decimal ActualAmount => _cart.ActualAmount;

    partial void OnSelectedItemChanged(SellableItemDto? value)
    {
        AddSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSessionChanged(PosSessionState value)
    {
        OnPropertyChanged(nameof(OnlineText));
        OnPropertyChanged(nameof(PendingSyncText));
    }

    public void LoadMatches(IEnumerable<SellableItemDto> items)
    {
        Matches.ReplaceWith(items.Take(8));
    }

    public void RefreshCart()
    {
        CartLines.ReplaceWith(_cart.Lines);
        OnPropertyChanged(nameof(TotalAmount));
        OnPropertyChanged(nameof(DiscountAmount));
        OnPropertyChanged(nameof(ActualAmount));
        ClearCartCommand.NotifyCanExecuteChanged();
        OpenPaymentCommand.NotifyCanExecuteChanged();
    }

    private void AppendScanText(string? value)
    {
        if (value == "Enter")
        {
            SearchAndAdd();
            return;
        }

        if (value == "Back")
        {
            ScanText = ScanText.Length > 0 ? ScanText[..^1] : string.Empty;
            return;
        }

        if (value == "Clear")
        {
            ScanText = string.Empty;
            IsMatchesPopupOpen = false;
            return;
        }

        ScanText += value;
    }

    private void ToggleTouchKeyboard()
    {
        IsTouchKeyboardOpen = !IsTouchKeyboardOpen;
        if (IsTouchKeyboardOpen)
        {
            IsMatchesPopupOpen = false;
        }
    }

    private void SearchAndAdd()
    {
        var matches = _priceIndex.Search(ScanText);
        Matches.ReplaceWith(matches);
        SelectedItem = matches.FirstOrDefault();

        if (SelectedItem is null)
        {
            IsMatchesPopupOpen = false;
            SetStatus("pos.status.noLocalMatch");
            return;
        }

        if (matches.Count == 1 || IsExactLookup(SelectedItem, ScanText))
        {
            IsMatchesPopupOpen = false;
            AddItem(SelectedItem);
            ScanText = string.Empty;
        }
        else
        {
            IsMatchesPopupOpen = true;
            SetStatus("pos.status.multipleMatches", matches.Count);
        }
    }

    private void AddSelected()
    {
        if (SelectedItem is null)
        {
            return;
        }

        AddItem(SelectedItem);
    }

    private void SelectMatch(SellableItemDto? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedItem = item;
        AddItem(item);
        ScanText = string.Empty;
        IsMatchesPopupOpen = false;
    }

    private void AddItem(SellableItemDto item)
    {
        _cart.AddItem(item);
        RefreshCart();
        SetStatus("pos.status.added", item.DisplayName);
        BeginRemoteLookup(item);
    }

    private void ClearCart()
    {
        _cart.Clear();
        RefreshCart();
        SetStatus("pos.status.cartCleared");
    }

    private void OpenPayment()
    {
        PaymentRequested?.Invoke(this, EventArgs.Empty);
        _onOpenPayment?.Invoke();
    }

    private void BeginRemoteLookup(SellableItemDto item)
    {
        if (!Session.IsOnline || _remoteLookupRefreshAsync is null)
        {
            return;
        }

        _ = RefreshRemoteLookupAsync(Session.StoreCode, item.LookupCode);
    }

    private async Task RefreshRemoteLookupAsync(string storeCode, string lookupCode)
    {
        try
        {
            var result = await _remoteLookupRefreshAsync!(storeCode, lookupCode, CancellationToken.None);
            if (result.Updated && result.Item is not null)
            {
                _cart.UpdateLineFromRemote(result.Item);
                SetStatusText($"Remote lookup refreshed: {result.Item.DisplayName}");
            }
            else if (result.Deleted)
            {
                _cart.RemoveLineByLookupCode(result.StoreCode, result.LookupCode);
                SetStatusText($"Remote lookup removed: {result.LookupCode}");
            }

            var catalogItems = await ReloadCatalogAsync(CancellationToken.None);
            RefreshMatches(catalogItems);
            RefreshCart();
        }
        catch (Exception ex)
        {
            SetStatusText($"Remote lookup failed: {ex.Message}");
        }
    }

    private async Task SyncAsync()
    {
        if (!Session.IsOnline)
        {
            SetStatusText("Offline: catalog sync skipped.");
            return;
        }

        if (_syncCatalogAsync is null)
        {
            SetStatus("pos.status.ready");
            return;
        }

        try
        {
            SetStatusText("Syncing catalog...");
            var catalogItems = await _syncCatalogAsync(CancellationToken.None);
            RefreshMatches(catalogItems);
            RefreshCart();
            SetStatusText("Catalog sync completed.");
        }
        catch (Exception ex)
        {
            SetStatusText($"Catalog sync failed: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<SellableItemDto>> ReloadCatalogAsync(CancellationToken cancellationToken)
    {
        return _reloadCatalogAsync is null
            ? _priceIndex.Items
            : await _reloadCatalogAsync(cancellationToken);
    }

    private void RefreshMatches(IReadOnlyList<SellableItemDto> catalogItems)
    {
        var matches = string.IsNullOrWhiteSpace(ScanText)
            ? catalogItems.Take(8)
            : _priceIndex.Search(ScanText);
        Matches.ReplaceWith(matches);
        SelectedItem = Matches.FirstOrDefault();
    }

    private static bool IsExactLookup(SellableItemDto item, string query)
    {
        var normalized = query.Trim();
        return string.Equals(item.Barcode, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.LookupCode, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.ItemNumber, normalized, StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string key, params object[] args)
    {
        _statusText = null;
        _statusKey = key;
        _statusArgs = args;
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void SetStatusText(string message)
    {
        _statusText = message;
        OnPropertyChanged(nameof(StatusMessage));
    }

    private string T(string key)
    {
        return _localization?.T(key) ?? key;
    }

    private string Format(string key, params object[] args)
    {
        var template = T(key);
        return args.Length == 0
            ? template
            : string.Format(_localization?.CurrentCulture ?? System.Globalization.CultureInfo.CurrentCulture, template, args);
    }

    private void RaiseLocalizedProperties()
    {
        OnPropertyChanged(nameof(ScreenTitleText));
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(SearchButtonText));
        OnPropertyChanged(nameof(AddSelectedText));
        OnPropertyChanged(nameof(CartTitleText));
        OnPropertyChanged(nameof(TotalsLabelText));
        OnPropertyChanged(nameof(PayNowText));
        OnPropertyChanged(nameof(ClearCartText));
        OnPropertyChanged(nameof(HoldOrderText));
        OnPropertyChanged(nameof(RecallOrderText));
        OnPropertyChanged(nameof(MemberText));
        OnPropertyChanged(nameof(SyncText));
        OnPropertyChanged(nameof(OnlineText));
        OnPropertyChanged(nameof(PendingSyncText));
        OnPropertyChanged(nameof(StatusMessage));
    }
}
