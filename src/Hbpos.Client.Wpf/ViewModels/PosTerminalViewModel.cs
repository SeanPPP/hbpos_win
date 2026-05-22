using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class PosTerminalViewModel : ObservableObject, IDisposable
{
    public const string PageId = "PosTerminal";

    private readonly LocalSellableItemIndex _priceIndex;
    private readonly PosCartService _cart;
    private readonly Action? _onOpenPayment;
    private readonly ILocalizationService? _localization;
    private readonly IRawScannerService? _rawScannerService;
    private readonly Func<string, string, CancellationToken, Task<RemoteLookupRefreshResult>>? _remoteLookupRefreshAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? _reloadCatalogAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? _syncCatalogAsync;
    private readonly Func<CancellationToken, Task<bool>>? _refreshOnlineAsync;
    private string _statusKey = "pos.status.ready";
    private object[] _statusArgs = [];
    private string? _statusText;

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private string _scanText = string.Empty;

    [ObservableProperty]
    private string _keypadBuffer = string.Empty;

    [ObservableProperty]
    private SellableItemDto? _selectedItem;

    [ObservableProperty]
    private CartLine? _selectedCartLine;

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
        Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? syncCatalogAsync = null,
        Func<CancellationToken, Task<bool>>? refreshOnlineAsync = null,
        IRawScannerService? rawScannerService = null)
    {
        _priceIndex = priceIndex;
        _cart = cart;
        _session = session;
        _onOpenPayment = onOpenPayment;
        _localization = localization;
        _remoteLookupRefreshAsync = remoteLookupRefreshAsync;
        _reloadCatalogAsync = reloadCatalogAsync;
        _syncCatalogAsync = syncCatalogAsync;
        _refreshOnlineAsync = refreshOnlineAsync;
        _rawScannerService = rawScannerService;
        if (_localization is not null)
        {
            _localization.CultureChanged += (_, _) => RaiseLocalizedProperties();
        }

        _cart.CartChanged += OnCartChanged;
        _rawScannerService?.Subscribe(PageId, OnRawBarcodeScanned);

        ScanCommand = new RelayCommand(SearchAndAdd);
        NumberInputCommand = new RelayCommand<string>(AppendScanText);
        KeypadInputCommand = new RelayCommand<string>(AppendKeypadBuffer);
        ToggleTouchKeyboardCommand = new RelayCommand(ToggleTouchKeyboard);
        AddSelectedCommand = new RelayCommand(AddSelected, () => SelectedItem is not null);
        SelectMatchCommand = new RelayCommand<SellableItemDto>(SelectMatch);
        RemoveLineCommand = new RelayCommand<CartLine>(RemoveLine);
        IncreaseLineCommand = new RelayCommand<CartLine>(IncreaseLine, line => line is not null && _cart.Lines.Contains(line));
        DecreaseLineCommand = new RelayCommand<CartLine>(DecreaseLine, line => line is not null && _cart.Lines.Contains(line));
        ClearSearchCommand = new RelayCommand(ClearSearch, () => !string.IsNullOrWhiteSpace(ScanText));
        ClearCartCommand = new RelayCommand(ClearCart, () => !_cart.IsEmpty);
        OpenPaymentCommand = new RelayCommand(OpenPayment, () => !_cart.IsEmpty);
        SyncCommand = new AsyncRelayCommand(SyncAsync);
    }

    public ObservableCollection<SellableItemDto> Matches { get; } = [];

    public ObservableCollection<CartLine> CartLines { get; } = [];

    public IRelayCommand ScanCommand { get; }

    public IRelayCommand<string> NumberInputCommand { get; }

    public IRelayCommand<string> KeypadInputCommand { get; }

    public IRelayCommand ToggleTouchKeyboardCommand { get; }

    public IRelayCommand AddSelectedCommand { get; }

    public IRelayCommand<SellableItemDto> SelectMatchCommand { get; }

    public IRelayCommand<CartLine> RemoveLineCommand { get; }

    public IRelayCommand<CartLine> IncreaseLineCommand { get; }

    public IRelayCommand<CartLine> DecreaseLineCommand { get; }

    public IRelayCommand ClearSearchCommand { get; }

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

    public decimal CartItemQuantity => _cart.Lines.Sum(line => line.Quantity);

    public int CartSkuCount => _cart.Lines.Count;

    public void Dispose()
    {
        _cart.CartChanged -= OnCartChanged;
        _rawScannerService?.Unsubscribe(PageId);
    }

    partial void OnSelectedItemChanged(SellableItemDto? value)
    {
        AddSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnScanTextChanged(string value)
    {
        ClearSearchCommand.NotifyCanExecuteChanged();
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
        SyncCartLines(_cart.Lines);
        RefreshCartState();
    }

    private void RefreshCartState()
    {
        OnPropertyChanged(nameof(TotalAmount));
        OnPropertyChanged(nameof(DiscountAmount));
        OnPropertyChanged(nameof(ActualAmount));
        OnPropertyChanged(nameof(CartItemQuantity));
        OnPropertyChanged(nameof(CartSkuCount));
        IncreaseLineCommand.NotifyCanExecuteChanged();
        DecreaseLineCommand.NotifyCanExecuteChanged();
        ClearCartCommand.NotifyCanExecuteChanged();
        OpenPaymentCommand.NotifyCanExecuteChanged();
    }

    private void SyncCartLines(IReadOnlyList<CartLine> sourceLines)
    {
        for (var i = CartLines.Count - 1; i >= 0; i--)
        {
            if (!sourceLines.Contains(CartLines[i]))
            {
                CartLines.RemoveAt(i);
            }
        }

        for (var sourceIndex = 0; sourceIndex < sourceLines.Count; sourceIndex++)
        {
            var line = sourceLines[sourceIndex];
            var currentIndex = CartLines.IndexOf(line);
            if (currentIndex < 0)
            {
                CartLines.Insert(Math.Min(sourceIndex, CartLines.Count), line);
            }
            else if (currentIndex != sourceIndex)
            {
                CartLines.Move(currentIndex, sourceIndex);
            }
        }

        if (SelectedCartLine is not null && !sourceLines.Contains(SelectedCartLine))
        {
            SelectedCartLine = null;
        }
    }

    private void OnCartChanged(object? sender, EventArgs e)
    {
        RefreshCart();
    }

    private void AppendScanText(string? value)
    {
        if (value == "Enter")
        {
            SearchAndAdd();
            IsTouchKeyboardOpen = false;
            return;
        }

        if (value == "Back")
        {
            ScanText = ScanText.Length > 0 ? ScanText[..^1] : string.Empty;
            return;
        }

        if (value == "Space")
        {
            ScanText += " ";
            return;
        }

        if (value == "Clear")
        {
            ScanText = string.Empty;
            IsMatchesPopupOpen = false;
            IsTouchKeyboardOpen = false;
            return;
        }

        ScanText += value;
    }

    private void AppendKeypadBuffer(string? value)
    {
        if (value == "QuickHalf")
        {
            ReplaceKeypadDecimal("50");
            return;
        }

        if (value == "QuickNinetyNine")
        {
            ReplaceKeypadDecimal("99");
            return;
        }

        if (value == "Back")
        {
            KeypadBuffer = KeypadBuffer.Length > 0 ? KeypadBuffer[..^1] : string.Empty;
            return;
        }

        if (value == "Clear")
        {
            KeypadBuffer = string.Empty;
            return;
        }

        if (string.IsNullOrEmpty(value) || value == "Enter" || value == "Space")
        {
            return;
        }

        if (value == ".")
        {
            if (!KeypadBuffer.Contains('.'))
            {
                KeypadBuffer = KeypadBuffer.Length == 0 ? "0." : KeypadBuffer + ".";
            }

            return;
        }

        if (value.Length != 1 || !char.IsDigit(value[0]) || HasTwoDecimalDigits(KeypadBuffer))
        {
            return;
        }

        KeypadBuffer += value;
    }

    private void ReplaceKeypadDecimal(string decimalDigits)
    {
        var wholePart = KeypadBuffer.Split('.')[0];
        KeypadBuffer = $"{(string.IsNullOrEmpty(wholePart) ? "0" : wholePart)}.{decimalDigits}";
    }

    private static bool HasTwoDecimalDigits(string value)
    {
        var decimalPointIndex = value.IndexOf('.', StringComparison.Ordinal);
        return decimalPointIndex >= 0 && value.Length - decimalPointIndex - 1 >= 2;
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
        ProcessScan(ScanText, preferExactLookup: false, source: "manual");
    }

    private void ProcessScan(string scanText, bool preferExactLookup, string source)
    {
        var submittedScanText = scanText;
        var totalStopwatch = Stopwatch.StartNew();
        var exactLookupElapsedMs = 0L;
        var searchElapsedMs = 0L;
        var cartUpdateElapsedMs = 0L;
        var uiRefreshElapsedMs = 0L;
        var matchKind = preferExactLookup ? "exact-not-found" : "search";
        var matchCount = 0;
        var autoAdded = false;

        if (preferExactLookup)
        {
            var exactLookupStopwatch = Stopwatch.StartNew();
            var exactMatches = _priceIndex.FindExactMatches(Session.StoreCode, submittedScanText);
            exactLookupStopwatch.Stop();
            exactLookupElapsedMs = exactLookupStopwatch.ElapsedMilliseconds;
            matchKind = exactMatches.Count switch
            {
                0 => "exact-not-found",
                1 => "lookup-exact",
                _ => "search-multiple"
            };

            var matches = exactMatches;
            var allowAutoAdd = exactMatches.Count == 1;
            if (exactMatches.Count == 0)
            {
                var metadataMatches = _priceIndex.FindMetadataExactMatches(Session.StoreCode, submittedScanText);
                if (metadataMatches.Count > 0)
                {
                    matches = metadataMatches;
                    matchKind = metadataMatches.Count > 1 ? "metadata-duplicate" : "metadata-only";
                }
            }

            ApplyScanMatches(matches, submittedScanText, allowAutoAdd, out autoAdded, out cartUpdateElapsedMs, out uiRefreshElapsedMs);
            matchCount = matches.Count;
        }
        else
        {
            var exactLookupStopwatch = Stopwatch.StartNew();
            var exactMatches = _priceIndex.FindExactMatches(Session.StoreCode, submittedScanText);
            exactLookupStopwatch.Stop();
            exactLookupElapsedMs = exactLookupStopwatch.ElapsedMilliseconds;
            var hasDuplicateExactMatch = exactMatches.Count > 1;
            if (hasDuplicateExactMatch)
            {
                matchKind = "search-multiple";
            }

            var searchStopwatch = Stopwatch.StartNew();
            var matches = _priceIndex.Search(submittedScanText);
            searchStopwatch.Stop();
            searchElapsedMs = searchStopwatch.ElapsedMilliseconds;

            ApplyScanMatches(matches, submittedScanText, allowAutoAdd: !hasDuplicateExactMatch, out autoAdded, out cartUpdateElapsedMs, out uiRefreshElapsedMs);
            matchCount = matches.Count;
        }

        totalStopwatch.Stop();
        ConsoleLog.Write(
            "PosScan",
            $"barcode={submittedScanText} storeCode={Session.StoreCode} source={source} hit={matchKind} matchCount={matchCount} autoAdded={autoAdded} cartLines={_cart.Lines.Count} exactLookupElapsedMs={exactLookupElapsedMs} searchElapsedMs={searchElapsedMs} cartUpdateElapsedMs={cartUpdateElapsedMs} uiRefreshElapsedMs={uiRefreshElapsedMs} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
    }

    private void ApplyScanMatches(
        IReadOnlyList<SellableItemDto> matches,
        string submittedScanText,
        bool allowAutoAdd,
        out bool autoAdded,
        out long cartUpdateElapsedMs,
        out long uiRefreshElapsedMs)
    {
        var uiRefreshStopwatch = Stopwatch.StartNew();
        autoAdded = false;
        cartUpdateElapsedMs = 0;
        Matches.ReplaceWith(matches);
        SelectedItem = matches.FirstOrDefault();

        if (SelectedItem is null)
        {
            IsMatchesPopupOpen = false;
            SetStatus("pos.status.noLocalMatch");
            uiRefreshStopwatch.Stop();
            uiRefreshElapsedMs = uiRefreshStopwatch.ElapsedMilliseconds;
            return;
        }

        if (allowAutoAdd && (matches.Count == 1 || IsExactLookup(SelectedItem, submittedScanText)))
        {
            IsMatchesPopupOpen = false;
            var cartUpdateStopwatch = Stopwatch.StartNew();
            AddItem(SelectedItem);
            cartUpdateStopwatch.Stop();
            cartUpdateElapsedMs = cartUpdateStopwatch.ElapsedMilliseconds;
            ScanText = string.Empty;
            autoAdded = true;
        }
        else
        {
            IsMatchesPopupOpen = true;
            SetStatus("pos.status.multipleMatches", matches.Count);
        }

        uiRefreshStopwatch.Stop();
        uiRefreshElapsedMs = uiRefreshStopwatch.ElapsedMilliseconds;
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
        IsTouchKeyboardOpen = false;
    }

    private void RemoveLine(CartLine? line)
    {
        if (line is null || !_cart.RemoveLine(line))
        {
            return;
        }

        SetStatus("pos.status.ready");
    }

    private void IncreaseLine(CartLine? line)
    {
        if (line is null || !_cart.IncreaseLine(line))
        {
            return;
        }

        SelectCartLine(line);
        SetStatus("pos.status.ready");
    }

    private void DecreaseLine(CartLine? line)
    {
        if (line is null || !_cart.DecreaseLine(line))
        {
            return;
        }

        if (_cart.Lines.Contains(line))
        {
            SelectCartLine(line);
        }

        SetStatus("pos.status.ready");
    }

    private void AddItem(SellableItemDto item)
    {
        var line = _cart.AddItem(item);
        SelectCartLine(line);
        IsTouchKeyboardOpen = false;
        SetStatus("pos.status.added", item.DisplayName);
        BeginRemoteLookup(item);
    }

    private void SelectCartLine(CartLine line)
    {
        if (ReferenceEquals(SelectedCartLine, line))
        {
            SelectedCartLine = null;
        }

        SelectedCartLine = line;
    }

    private void OnRawBarcodeScanned(RawBarcodeScannedEventArgs e)
    {
        ScanText = e.Barcode;
        IsTouchKeyboardOpen = false;
        ProcessScan(e.Barcode, preferExactLookup: true, source: "raw");
    }

    private void ClearSearch()
    {
        ScanText = string.Empty;
        IsMatchesPopupOpen = false;
        IsTouchKeyboardOpen = false;
    }

    private void ClearCart()
    {
        _cart.Clear();
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
        }
        catch (Exception ex)
        {
            SetStatusText($"Remote lookup failed: {ex.Message}");
        }
    }

    private async Task SyncAsync()
    {
        if (_refreshOnlineAsync is not null)
        {
            var isOnline = await _refreshOnlineAsync(CancellationToken.None);
            Session = Session with { IsOnline = isOnline };
        }

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
