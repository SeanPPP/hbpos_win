using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class ReceiptReturnsViewModel : ObservableObject, IScannerInputTarget, IDisposable
{
    public const string PageId = "ReceiptReturns";
    private const string DefaultStatusMessage = "Scan an order number to start a receipt return.";
    private const string DefaultOrderSummaryText = "No receipt loaded";

    private readonly IReceiptReturnsWorkflowService _workflowService;
    private readonly IRawScannerService? _rawScannerService;
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
    private string _statusMessage = DefaultStatusMessage;

    [ObservableProperty]
    private string _orderSummaryText = DefaultOrderSummaryText;

    [ObservableProperty]
    private bool _returnRecordsMayBeStale;

    public ReceiptReturnsViewModel(
        IReceiptReturnsWorkflowService workflowService,
        PosSessionState session,
        Action onBack,
        Action<CartLine>? onReturnLineAdded = null,
        IRawScannerService? rawScannerService = null)
    {
        _workflowService = workflowService;
        _session = session;
        _onBack = onBack;
        _onReturnLineAdded = onReturnLineAdded;
        _rawScannerService = rawScannerService;

        LookupCommand = new AsyncRelayCommand(LookupAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(ScanText));
        AddReceiptLineCommand = new RelayCommand<ReceiptReturnOrderLineViewModel>(AddReceiptLine, CanAddReceiptLine);
        RemovePendingLineCommand = new RelayCommand<PendingReturnLineViewModel>(RemovePendingLine);
        ConfirmToCartCommand = new RelayCommand(ConfirmToCart, () => PendingLines.Count > 0 && !IsBusy);
        BackCommand = new RelayCommand(Back);
        ClearCommand = new RelayCommand(ClearSelection);

        _rawScannerService?.Subscribe(PageId, OnRawBarcodeScanned);
    }

    public string ScannerPageId => PageId;

    public ObservableCollection<ReceiptReturnOrderLineViewModel> OrderLines { get; } = [];

    public ObservableCollection<PendingReturnLineViewModel> PendingLines { get; } = [];

    public IAsyncRelayCommand LookupCommand { get; }

    public IRelayCommand<ReceiptReturnOrderLineViewModel> AddReceiptLineCommand { get; }

    public IRelayCommand<PendingReturnLineViewModel> RemovePendingLineCommand { get; }

    public IRelayCommand ConfirmToCartCommand { get; }

    public IRelayCommand BackCommand { get; }

    public IRelayCommand ClearCommand { get; }

    public decimal PendingTotal => PendingLines.Sum(line => line.NegativeSubtotal);

    public int PendingSkuCount => PendingLines.Count;

    public void Dispose()
    {
        _rawScannerService?.Unsubscribe(PageId);
    }

    public void ResetToDefault()
    {
        ScanText = string.Empty;
        IsNoReceiptMode = false;
        ClearSelection();
        StatusMessage = DefaultStatusMessage;
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
    }

    partial void OnIsNoReceiptModeChanged(bool value)
    {
        ClearSelection();
        StatusMessage = value
            ? "No-receipt return is on. Scan products to add them to the return area."
            : "Receipt return is on. Scan an order number.";
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
            OrderSummaryText = DefaultOrderSummaryText;
            OnPendingLinesChanged();
            return;
        }

        _currentOrder = result.Order;
        OrderSummaryText = $"#{result.Order.OrderGuid.ToString("N")[..8].ToUpperInvariant()}  {result.Order.SoldAt.ToLocalTime():yyyy-MM-dd HH:mm}  {result.Order.CashierName}";
        foreach (var line in result.Order.Lines)
        {
            OrderLines.Add(new ReceiptReturnOrderLineViewModel(result.Order, line));
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
        StatusMessage = $"Added return item: {line.DisplayName}";
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
        _currentOrder = null;
        ReturnRecordsMayBeStale = false;
        OrderSummaryText = DefaultOrderSummaryText;
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
    }

    private void OnRawBarcodeScanned(RawBarcodeScannedEventArgs args)
    {
        ProcessScannerBarcode(args.Barcode, args.DevicePath, "raw");
    }
}

public sealed partial class ReceiptReturnOrderLineViewModel : ObservableObject
{
    private readonly ReceiptReturnOrderLine _line;

    [ObservableProperty]
    private decimal _pendingQuantity;

    public ReceiptReturnOrderLineViewModel(ReceiptReturnOrder order, ReceiptReturnOrderLine line)
    {
        _line = line;
        OrderGuid = order.OrderGuid;
        StoreCode = order.StoreCode;
        DeviceCode = order.DeviceCode;
    }

    public Guid OrderGuid { get; }

    public string StoreCode { get; }

    public string DeviceCode { get; }

    public Guid OrderLineGuid => _line.OrderLineGuid;

    public string ProductCode => _line.ProductCode;

    public string? ReferenceCode => _line.ReferenceCode;

    public string DisplayName => _line.DisplayName;

    public string LookupCode => _line.LookupCode;

    public string? ItemNumber => _line.ItemNumber;

    public decimal OriginalQuantity => _line.OriginalQuantity;

    public decimal ReturnedQuantity => _line.ReturnedQuantity;

    public decimal AvailableQuantity => _line.AvailableQuantity;

    public decimal AvailableRemaining => Math.Max(0m, AvailableQuantity - PendingQuantity);

    public decimal UnitPrice => _line.UnitPrice;

    public decimal ReturnUnitAmount => _line.ReturnUnitAmount;

    public PriceSourceKind PriceSource => PriceSourceKind.StoreRetailPrice;

    public string PriceSourceLabel => PriceSourceKind.StoreRetailPrice.ToString();

    partial void OnPendingQuantityChanged(decimal value)
    {
        OnPropertyChanged(nameof(AvailableRemaining));
    }
}

public sealed partial class PendingReturnLineViewModel : ObservableObject
{
    [ObservableProperty]
    private decimal _quantity;

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
        Quantity = quantity;
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

    partial void OnQuantityChanged(decimal value)
    {
        OnPropertyChanged(nameof(NegativeSubtotal));
    }
}
