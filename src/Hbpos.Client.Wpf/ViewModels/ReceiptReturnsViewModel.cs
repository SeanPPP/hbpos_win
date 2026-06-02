using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.ViewModels;

public enum OpenItemKeyboardTarget
{
    Description,
    Amount
}

public sealed partial class ReceiptReturnsViewModel : ObservableObject, IScannerInputTarget, IDisposable
{
    public const string PageId = "ReceiptReturns";

    private const string DefaultStatusMessage = "Scan an order number to start a receipt return.";
    private const string DefaultOrderSummaryText = "No order loaded";
    private const string DefaultOpenItemName = "Open Item";

    private readonly IReceiptReturnsWorkflowService _workflowService;
    private readonly IRawScannerService? _rawScannerService;
    private readonly ILocalizationService? _localization;
    private readonly Action _onBack;
    private readonly Action<CartLine>? _onReturnLineAdded;
    private ReceiptReturnOrder? _currentOrder;

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private string _scanText = string.Empty;

    [ObservableProperty]
    private bool _isNoReceiptMode;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _orderSummaryText = string.Empty;

    [ObservableProperty]
    private bool _returnRecordsMayBeStale;

    [ObservableProperty]
    private bool _isOpenItemDialogOpen;

    [ObservableProperty]
    private string _openItemDisplayName = string.Empty;

    [ObservableProperty]
    private string _openItemUnitPriceText = string.Empty;

    [ObservableProperty]
    private OpenItemKeyboardTarget _openItemKeyboardTarget = OpenItemKeyboardTarget.Description;

    public ReceiptReturnsViewModel(
        IReceiptReturnsWorkflowService workflowService,
        PosSessionState session,
        Action onBack,
        Action<CartLine>? onReturnLineAdded = null,
        IRawScannerService? rawScannerService = null,
        ILocalizationService? localization = null)
    {
        _workflowService = workflowService;
        _session = session;
        _onBack = onBack;
        _onReturnLineAdded = onReturnLineAdded;
        _rawScannerService = rawScannerService;
        _localization = localization;
        if (_localization is not null)
        {
            _localization.CultureChanged += (_, _) => RefreshLocalizedState();
        }

        LookupCommand = new AsyncRelayCommand(LookupAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(ScanText));
        AddReceiptLineCommand = new RelayCommand<ReceiptReturnOrderLineViewModel>(AddReceiptLine, CanAddReceiptLine);
        RemovePendingLineCommand = new RelayCommand<PendingReturnLineViewModel>(RemovePendingLine);
        ConfirmToCartCommand = new RelayCommand(ConfirmToCart, () => PendingLines.Count > 0 && !IsBusy);
        OpenNoReceiptOpenItemDialogCommand = new RelayCommand(OpenNoReceiptOpenItemDialog, CanOpenNoReceiptOpenItemDialog);
        CancelNoReceiptOpenItemDialogCommand = new RelayCommand(CancelNoReceiptOpenItemDialog);
        ConfirmNoReceiptOpenItemCommand = new RelayCommand(ConfirmNoReceiptOpenItem, CanConfirmNoReceiptOpenItem);
        SelectOpenItemDescriptionKeyboardCommand = new RelayCommand(() => OpenItemKeyboardTarget = OpenItemKeyboardTarget.Description);
        SelectOpenItemAmountKeyboardCommand = new RelayCommand(() => OpenItemKeyboardTarget = OpenItemKeyboardTarget.Amount);
        OpenItemKeyboardInputCommand = new RelayCommand<string>(AppendOpenItemKeyboardInput);
        BackCommand = new RelayCommand(Back);
        ClearCommand = new RelayCommand(ClearSelection);

        _rawScannerService?.Subscribe(PageId, OnRawBarcodeScanned);
        StatusMessage = T("returns.status.default", DefaultStatusMessage);
        OrderSummaryText = T("returns.orderSummary.none", DefaultOrderSummaryText);
    }

    public string ScannerPageId => PageId;

    public ObservableCollection<ReceiptReturnOrderLineViewModel> OrderLines { get; } = [];

    public ObservableCollection<PendingReturnLineViewModel> PendingLines { get; } = [];

    public IAsyncRelayCommand LookupCommand { get; }

    public IRelayCommand<ReceiptReturnOrderLineViewModel> AddReceiptLineCommand { get; }

    public IRelayCommand<PendingReturnLineViewModel> RemovePendingLineCommand { get; }

    public IRelayCommand ConfirmToCartCommand { get; }

    public IRelayCommand OpenNoReceiptOpenItemDialogCommand { get; }

    public IRelayCommand CancelNoReceiptOpenItemDialogCommand { get; }

    public IRelayCommand ConfirmNoReceiptOpenItemCommand { get; }

    public IRelayCommand SelectOpenItemDescriptionKeyboardCommand { get; }

    public IRelayCommand SelectOpenItemAmountKeyboardCommand { get; }

    public IRelayCommand<string> OpenItemKeyboardInputCommand { get; }

    public IRelayCommand BackCommand { get; }

    public IRelayCommand ClearCommand { get; }

    public decimal PendingTotal => PendingLines.Sum(line => line.NegativeSubtotal);

    public int PendingSkuCount => PendingLines.Count;

    public bool IsOpenItemDescriptionKeyboardVisible => OpenItemKeyboardTarget == OpenItemKeyboardTarget.Description;

    public bool IsOpenItemAmountKeyboardVisible => OpenItemKeyboardTarget == OpenItemKeyboardTarget.Amount;

    public void Dispose()
    {
        _rawScannerService?.Unsubscribe(PageId);
    }

    public void ResetToDefault()
    {
        ScanText = string.Empty;
        IsNoReceiptMode = false;
        ClearSelection();
        StatusMessage = T("returns.status.default", DefaultStatusMessage);
    }

    public bool ProcessScannerBarcode(string barcode, string devicePath, string source)
    {
        var normalizedBarcode = barcode.Trim();
        if (string.IsNullOrWhiteSpace(normalizedBarcode))
        {
            return true;
        }

        ScanText = normalizedBarcode;
        _ = LookupAsync();
        return true;
    }

    partial void OnScanTextChanged(string value)
    {
        LookupCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        LookupCommand.NotifyCanExecuteChanged();
        AddReceiptLineCommand.NotifyCanExecuteChanged();
        ConfirmToCartCommand.NotifyCanExecuteChanged();
        OpenNoReceiptOpenItemDialogCommand.NotifyCanExecuteChanged();
        ConfirmNoReceiptOpenItemCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsNoReceiptModeChanged(bool value)
    {
        ClearSelection();
        if (!value)
        {
            ResetOpenItemDialog();
        }

        StatusMessage = value
            ? T("returns.status.noReceiptMode", "No-receipt return is on. Scan products to add them to the return area.")
            : T("returns.status.receiptMode", "Receipt return is on. Scan an order number.");
        OpenNoReceiptOpenItemDialogCommand.NotifyCanExecuteChanged();
        ConfirmNoReceiptOpenItemCommand.NotifyCanExecuteChanged();
    }

    partial void OnOpenItemDisplayNameChanged(string value)
    {
        ConfirmNoReceiptOpenItemCommand.NotifyCanExecuteChanged();
    }

    partial void OnOpenItemUnitPriceTextChanged(string value)
    {
        ConfirmNoReceiptOpenItemCommand.NotifyCanExecuteChanged();
    }

    partial void OnOpenItemKeyboardTargetChanged(OpenItemKeyboardTarget value)
    {
        OnPropertyChanged(nameof(IsOpenItemDescriptionKeyboardVisible));
        OnPropertyChanged(nameof(IsOpenItemAmountKeyboardVisible));
    }

    private async Task LookupAsync()
    {
        if (string.IsNullOrWhiteSpace(ScanText) || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (IsNoReceiptMode)
            {
                AddNoReceiptProduct(ScanText);
                return;
            }

            var result = await _workflowService.LookupOrderAsync(Session, ScanText);
            ApplyOrderLookupResult(result);
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    private void ApplyOrderLookupResult(ReceiptReturnLookupResult result)
    {
        OrderLines.Clear();
        PendingLines.Clear();
        ReturnRecordsMayBeStale = result.ReturnRecordsMayBeStale;
        StatusMessage = result.StatusMessage;

        if (result.Order is null)
        {
            _currentOrder = null;
            OrderSummaryText = T("returns.orderSummary.none", DefaultOrderSummaryText);
            OnPendingLinesChanged();
            return;
        }

        _currentOrder = result.Order;
        OrderSummaryText = Format(
            "returns.orderSummary.format",
            "#{0}  {1:yyyy-MM-dd HH:mm}  {2}",
            result.Order.OrderGuid.ToString("N")[..8].ToUpperInvariant(),
            result.Order.SoldAt.ToLocalTime(),
            result.Order.CashierName);
        foreach (var line in result.Order.Lines)
        {
            OrderLines.Add(new ReceiptReturnOrderLineViewModel(
                result.Order,
                line,
                T("returns.priceSource.receipt", "Receipt return")));
        }

        OnPendingLinesChanged();
    }

    private void AddNoReceiptProduct(string query)
    {
        var result = _workflowService.LookupNoReceiptProduct(Session, query);
        StatusMessage = result.StatusMessage;
        if (result.Item is null)
        {
            return;
        }

        var item = result.Item;
        var sourceKey = $"noreceipt:{item.StoreCode}:{CartLine.NormalizeLookupCode(item.LookupCode)}";
        AddOrIncreasePendingLine(new PendingReturnLineViewModel(
            sourceKey,
            null,
            item.StoreCode,
            item.ProductCode,
            item.ReferenceCode,
            item.DisplayName,
            item.LookupCode,
            item.ItemNumber,
            item.ProductImage,
            1m,
            item.RetailPrice,
            item.PriceSource,
            item.PriceSourceLabel,
            null,
            null));
    }

    private bool CanOpenNoReceiptOpenItemDialog()
    {
        return IsNoReceiptMode && !IsBusy;
    }

    private void OpenNoReceiptOpenItemDialog()
    {
        if (!CanOpenNoReceiptOpenItemDialog())
        {
            return;
        }

        OpenItemDisplayName = T("returns.openItem.defaultName", DefaultOpenItemName);
        OpenItemUnitPriceText = string.Empty;
        OpenItemKeyboardTarget = OpenItemKeyboardTarget.Description;
        IsOpenItemDialogOpen = true;
        StatusMessage = T("returns.status.openItemPrompt", "Enter a no-barcode item name and retail price.");
    }

    private void AppendOpenItemKeyboardInput(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (OpenItemKeyboardTarget == OpenItemKeyboardTarget.Amount)
        {
            AppendOpenItemAmount(value);
            return;
        }

        AppendOpenItemDescription(value);
    }

    private void AppendOpenItemDescription(string value)
    {
        switch (value)
        {
            case "Back":
                if (OpenItemDisplayName.Length > 0)
                {
                    OpenItemDisplayName = OpenItemDisplayName[..^1];
                }

                return;
            case "Clear":
                OpenItemDisplayName = string.Empty;
                return;
            case "Space":
                OpenItemDisplayName += " ";
                return;
        }

        if (value.Length == 1 && value[0] is >= 'A' and <= 'Z')
        {
            OpenItemDisplayName += value;
        }
    }

    private void AppendOpenItemAmount(string value)
    {
        switch (value)
        {
            case "Back":
                if (OpenItemUnitPriceText.Length > 0)
                {
                    OpenItemUnitPriceText = OpenItemUnitPriceText[..^1];
                }

                return;
            case "Clear":
                OpenItemUnitPriceText = string.Empty;
                return;
            case ".":
                if (!OpenItemUnitPriceText.Contains('.', StringComparison.Ordinal))
                {
                    OpenItemUnitPriceText = string.IsNullOrEmpty(OpenItemUnitPriceText)
                        ? "0."
                        : OpenItemUnitPriceText + ".";
                }

                return;
        }

        if (value.Length != 1 || value[0] is < '0' or > '9')
        {
            return;
        }

        // 无码商品金额通过触屏数字键盘输入，固定限制到两位小数，避免生成无法结账的金额文本。
        var candidate = OpenItemUnitPriceText + value;
        var decimalIndex = candidate.IndexOf('.');
        if (decimalIndex >= 0 && candidate.Length - decimalIndex - 1 > 2)
        {
            return;
        }

        OpenItemUnitPriceText = candidate;
    }

    private void CancelNoReceiptOpenItemDialog()
    {
        ResetOpenItemDialog();
    }

    private bool CanConfirmNoReceiptOpenItem()
    {
        return IsNoReceiptMode &&
            !IsBusy &&
            IsOpenItemDialogOpen &&
            !string.IsNullOrWhiteSpace(OpenItemDisplayName) &&
            TryParsePositiveAmount(OpenItemUnitPriceText, out _);
    }

    private void ConfirmNoReceiptOpenItem()
    {
        if (!CanConfirmNoReceiptOpenItem() ||
            !TryParsePositiveAmount(OpenItemUnitPriceText, out var unitPrice))
        {
            StatusMessage = T("returns.status.openItemPriceRequired", "Enter a retail price greater than zero.");
            return;
        }

        var result = _workflowService.CreateNoReceiptOpenItem(Session, OpenItemDisplayName, unitPrice);
        StatusMessage = result.StatusMessage;
        if (result.Line is null)
        {
            return;
        }

        AddOrIncreasePendingLine(ToPendingLineViewModel(result.Line));
        ResetOpenItemDialog();
        RefreshCommandStates();
    }

    private bool CanAddReceiptLine(ReceiptReturnOrderLineViewModel? line)
    {
        return !IsBusy && line is not null && line.AvailableRemaining > 0m;
    }

    private void AddReceiptLine(ReceiptReturnOrderLineViewModel? line)
    {
        if (line is null || line.AvailableRemaining <= 0m)
        {
            return;
        }

        var sourceKey = $"receipt:{line.OrderGuid:D}:{line.OrderLineGuid:D}";
        AddOrIncreasePendingLine(new PendingReturnLineViewModel(
            sourceKey,
            line,
            line.StoreCode,
            line.ProductCode,
            line.ReferenceCode,
            line.DisplayName,
            line.LookupCode,
            line.ItemNumber,
            null,
            1m,
            line.ReturnUnitAmount,
            line.PriceSource,
            line.PriceSourceLabel,
            line.OrderGuid,
            line.OrderLineGuid));
        line.PendingQuantity += 1m;
        StatusMessage = Format(
            "returns.status.addedReceiptLine",
            "Added return item: {0}",
            line.DisplayName);
        RefreshCommandStates();
    }

    private void AddOrIncreasePendingLine(PendingReturnLineViewModel candidate)
    {
        var existing = PendingLines.FirstOrDefault(line =>
            string.Equals(line.ReturnSourceKey, candidate.ReturnSourceKey, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Quantity += candidate.Quantity;
        }
        else
        {
            PendingLines.Add(candidate);
        }

        OnPendingLinesChanged();
    }

    private static PendingReturnLineViewModel ToPendingLineViewModel(PendingReturnLine line)
    {
        return new PendingReturnLineViewModel(
            line.ReturnSourceKey,
            null,
            line.StoreCode,
            line.ProductCode,
            line.ReferenceCode,
            line.DisplayName,
            line.LookupCode,
            line.ItemNumber,
            line.ProductImage,
            line.Quantity,
            line.UnitPrice,
            line.PriceSource,
            line.PriceSourceLabel,
            line.OriginalOrderGuid,
            line.OriginalOrderLineGuid);
    }

    private void RemovePendingLine(PendingReturnLineViewModel? line)
    {
        if (line is null)
        {
            return;
        }

        if (PendingLines.Remove(line) && line.ReceiptLine is not null)
        {
            line.ReceiptLine.PendingQuantity = Math.Max(0m, line.ReceiptLine.PendingQuantity - line.Quantity);
        }

        OnPendingLinesChanged();
        RefreshCommandStates();
    }

    private void ConfirmToCart()
    {
        if (PendingLines.Count == 0)
        {
            return;
        }

        var added = _workflowService.AddReturnLinesToCart(
            PendingLines.Select(line => line.ToPendingReturnLine()),
            _currentOrder?.PaymentCapacities);
        var lastAdded = added.LastOrDefault();
        ResetToDefault();
        if (lastAdded is not null)
        {
            _onReturnLineAdded?.Invoke(lastAdded);
        }

        _onBack();
    }

    private void Back()
    {
        ResetToDefault();
        _onBack();
    }

    private void ClearSelection()
    {
        OrderLines.Clear();
        PendingLines.Clear();
        ResetOpenItemDialog();
        _currentOrder = null;
        ReturnRecordsMayBeStale = false;
        OrderSummaryText = T("returns.orderSummary.none", DefaultOrderSummaryText);
        OnPendingLinesChanged();
        RefreshCommandStates();
    }

    private void OnPendingLinesChanged()
    {
        OnPropertyChanged(nameof(PendingTotal));
        OnPropertyChanged(nameof(PendingSkuCount));
        ConfirmToCartCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCommandStates()
    {
        LookupCommand.NotifyCanExecuteChanged();
        AddReceiptLineCommand.NotifyCanExecuteChanged();
        ConfirmToCartCommand.NotifyCanExecuteChanged();
        OpenNoReceiptOpenItemDialogCommand.NotifyCanExecuteChanged();
        ConfirmNoReceiptOpenItemCommand.NotifyCanExecuteChanged();
    }

    private void ResetOpenItemDialog()
    {
        IsOpenItemDialogOpen = false;
        OpenItemDisplayName = string.Empty;
        OpenItemUnitPriceText = string.Empty;
        OpenItemKeyboardTarget = OpenItemKeyboardTarget.Description;
    }

    private void RefreshLocalizedState()
    {
        if (_currentOrder is null)
        {
            OrderSummaryText = T("returns.orderSummary.none", DefaultOrderSummaryText);
        }
        else
        {
            OrderSummaryText = Format(
                "returns.orderSummary.format",
                "#{0}  {1:yyyy-MM-dd HH:mm}  {2}",
                _currentOrder.OrderGuid.ToString("N")[..8].ToUpperInvariant(),
                _currentOrder.SoldAt.ToLocalTime(),
                _currentOrder.CashierName);
        }

        if (string.Equals(StatusMessage, DefaultStatusMessage, StringComparison.Ordinal) ||
            string.Equals(StatusMessage, T("returns.status.default", DefaultStatusMessage), StringComparison.Ordinal))
        {
            StatusMessage = T("returns.status.default", DefaultStatusMessage);
        }
    }

    private void OnRawBarcodeScanned(RawBarcodeScannedEventArgs args)
    {
        ProcessScannerBarcode(args.Barcode, args.DevicePath, "raw");
    }

    private string T(string key, string fallback)
    {
        return _localization?.T(key) ?? fallback;
    }

    private string Format(string key, string fallback, params object[] args)
    {
        return string.Format(
            _localization?.CurrentCulture ?? CultureInfo.CurrentCulture,
            _localization?.T(key) ?? fallback,
            args);
    }

    private static bool TryParsePositiveAmount(string value, out decimal amount)
    {
        return (decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out amount) ||
                decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)) &&
            amount > 0m;
    }
}

public sealed partial class ReceiptReturnOrderLineViewModel : ObservableObject
{
    public ReceiptReturnOrderLineViewModel(
        ReceiptReturnOrder order,
        ReceiptReturnOrderLine line,
        string priceSourceLabel)
    {
        OrderGuid = order.OrderGuid;
        OrderLineGuid = line.OrderLineGuid;
        StoreCode = order.StoreCode;
        ProductCode = line.ProductCode;
        ReferenceCode = line.ReferenceCode;
        DisplayName = line.DisplayName;
        LookupCode = line.LookupCode;
        ItemNumber = line.ItemNumber;
        OriginalQuantity = line.OriginalQuantity;
        ReturnedQuantity = line.ReturnedQuantity;
        ReturnUnitAmount = line.ReturnUnitAmount;
        PriceSource = PriceSourceKind.ProductBase;
        PriceSourceLabel = priceSourceLabel;
    }

    public Guid OrderGuid { get; }

    public Guid OrderLineGuid { get; }

    public string StoreCode { get; }

    public string ProductCode { get; }

    public string? ReferenceCode { get; }

    public string DisplayName { get; }

    public string LookupCode { get; }

    public string? ItemNumber { get; }

    public decimal OriginalQuantity { get; }

    public decimal ReturnedQuantity { get; }

    public decimal ReturnUnitAmount { get; }

    public PriceSourceKind PriceSource { get; }

    public string PriceSourceLabel { get; }

    [ObservableProperty]
    private decimal _pendingQuantity;

    public decimal AvailableRemaining => Math.Max(0m, OriginalQuantity - ReturnedQuantity - PendingQuantity);
}

public sealed partial class PendingReturnLineViewModel : ObservableObject
{
    public PendingReturnLineViewModel(
        string returnSourceKey,
        ReceiptReturnOrderLineViewModel? receiptLine,
        string storeCode,
        string productCode,
        string? referenceCode,
        string displayName,
        string lookupCode,
        string? itemNumber,
        string? productImage,
        decimal quantity,
        decimal unitPrice,
        PriceSourceKind priceSource,
        string priceSourceLabel,
        Guid? originalOrderGuid,
        Guid? originalOrderLineGuid)
    {
        ReturnSourceKey = returnSourceKey;
        ReceiptLine = receiptLine;
        StoreCode = storeCode;
        ProductCode = productCode;
        ReferenceCode = referenceCode;
        DisplayName = displayName;
        LookupCode = lookupCode;
        ItemNumber = itemNumber;
        ProductImage = productImage;
        _quantity = quantity;
        UnitPrice = unitPrice;
        PriceSource = priceSource;
        PriceSourceLabel = priceSourceLabel;
        OriginalOrderGuid = originalOrderGuid;
        OriginalOrderLineGuid = originalOrderLineGuid;
    }

    public string ReturnSourceKey { get; }

    public ReceiptReturnOrderLineViewModel? ReceiptLine { get; }

    public string StoreCode { get; }

    public string ProductCode { get; }

    public string? ReferenceCode { get; }

    public string DisplayName { get; }

    public string LookupCode { get; }

    public string? ItemNumber { get; }

    public string? ProductImage { get; }

    [ObservableProperty]
    private decimal _quantity;

    public decimal UnitPrice { get; }

    public PriceSourceKind PriceSource { get; }

    public string PriceSourceLabel { get; }

    public Guid? OriginalOrderGuid { get; }

    public Guid? OriginalOrderLineGuid { get; }

    public decimal NegativeSubtotal => -decimal.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);

    public PendingReturnLine ToPendingReturnLine()
    {
        return new PendingReturnLine(
            StoreCode,
            ProductCode,
            ReferenceCode,
            DisplayName,
            LookupCode,
            ItemNumber,
            ProductImage,
            Quantity,
            UnitPrice,
            PriceSource,
            PriceSourceLabel,
            ReturnSourceKey,
            OriginalOrderGuid,
            OriginalOrderLineGuid);
    }
}
