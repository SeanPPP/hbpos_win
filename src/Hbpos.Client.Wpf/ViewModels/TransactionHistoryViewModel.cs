using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

public enum TransactionHistorySource
{
    LocalOrders,
    RemoteOrders
}

public sealed record HistorySourceOption(TransactionHistorySource Source, string Label);

public sealed record TerminalFilterOption(string? DeviceCode, string Label);

public sealed record HistoryOrderListItem(
    Guid OrderGuid,
    TransactionHistorySource Source,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset OccurredAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    int LineCount,
    string PaymentSummary,
    string StatusLabel,
    bool IsSuspendedOrder = false,
    bool CanRecall = false)
{
    public string ShortOrderId => OrderGuid.ToString("N")[..8].ToUpperInvariant();

    public string SoldAtDisplay => OccurredAt.ToLocalTime().ToString("MMM dd, yyyy HH:mm", CultureInfo.CurrentCulture);
}

public sealed partial class TransactionHistoryViewModel : ObservableObject
{
    private readonly IReceiptQueryService? _receiptQueryService;
    private readonly ISuspendedOrderService? _suspendedOrderService;
    private readonly IRemoteOrderHistoryService? _remoteOrderHistoryService;
    private readonly Func<Task>? _onSuspendedOrderRecalledAsync;
    private readonly Action? _returnToPos;
    private bool _suppressSelectedOrderLoad;
    private bool _suppressSourceAutoLoad;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _dateFilterText = "Today";

    [ObservableProperty]
    private DateTime? _dateFrom = DateTime.Today;

    [ObservableProperty]
    private DateTime? _dateTo = DateTime.Today;

    [ObservableProperty]
    private string _storeFilterText = "All Stores";

    [ObservableProperty]
    private string _terminalFilterText = "All Terminals";

    [ObservableProperty]
    private TerminalFilterOption? _selectedTerminalOption;

    [ObservableProperty]
    private HistorySourceOption? _selectedSourceOption;

    [ObservableProperty]
    private HistoryOrderListItem? _selectedOrder;

    [ObservableProperty]
    private decimal _previewSubtotal;

    [ObservableProperty]
    private decimal _previewDiscount;

    [ObservableProperty]
    private decimal _previewTotal;

    [ObservableProperty]
    private string _previewOrderId = "-";

    [ObservableProperty]
    private string _previewSoldAt = "-";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private PosSessionState _session = new("HB POS", "1002", "Main Branch", "Terminal 04", "C001", "Alice", false, 0);

    public TransactionHistoryViewModel()
        : this(null, null, null, null, null, null, initialize: true)
    {
    }

    public TransactionHistoryViewModel(ILocalOrderRepository orderRepository)
        : this(new ReceiptQueryService(orderRepository), null, null, null, null, null, initialize: true)
    {
    }

    public TransactionHistoryViewModel(IReceiptQueryService receiptQueryService)
        : this(receiptQueryService, null, null, null, null, null, initialize: true)
    {
    }

    public TransactionHistoryViewModel(
        IReceiptQueryService receiptQueryService,
        ISuspendedOrderService? suspendedOrderService,
        IRemoteOrderHistoryService? remoteOrderHistoryService,
        PosSessionState session,
        Func<Task>? onSuspendedOrderRecalledAsync = null,
        Action? returnToPos = null)
        : this(receiptQueryService, suspendedOrderService, remoteOrderHistoryService, session, onSuspendedOrderRecalledAsync, returnToPos, initialize: true)
    {
    }

    private TransactionHistoryViewModel(
        IReceiptQueryService? receiptQueryService,
        ISuspendedOrderService? suspendedOrderService,
        IRemoteOrderHistoryService? remoteOrderHistoryService,
        PosSessionState? session,
        Func<Task>? onSuspendedOrderRecalledAsync,
        Action? returnToPos,
        bool initialize)
    {
        _receiptQueryService = receiptQueryService;
        _suspendedOrderService = suspendedOrderService;
        _remoteOrderHistoryService = remoteOrderHistoryService;
        _onSuspendedOrderRecalledAsync = onSuspendedOrderRecalledAsync;
        _returnToPos = returnToPos;
        if (session is not null)
        {
            Session = session;
            StoreFilterText = $"{session.StoreName} ({session.StoreCode})";
            TerminalFilterText = session.DeviceCode;
        }

        RefreshTerminalOptions(selectAllTerminals: session is null);

        SourceOptions.Add(new HistorySourceOption(TransactionHistorySource.LocalOrders, "本地"));
        SourceOptions.Add(new HistorySourceOption(TransactionHistorySource.RemoteOrders, "在线"));
        _selectedSourceOption = SourceOptions[0];

        LoadCommand = new AsyncRelayCommand(() => LoadAsync());
        ReturnToPosCommand = new RelayCommand(ReturnToPos, CanReturnToPos);
        RecallSelectedCommand = new AsyncRelayCommand(RecallSelectedAsync, CanRecallSelected);
        RecallOrderCommand = new AsyncRelayCommand<HistoryOrderListItem>(RecallOrderAsync, CanRecallOrder);
        ReprintCommand = new RelayCommand(() => ReprintRequested?.Invoke(this, EventArgs.Empty), CanReprintSelected);
        RefundCommand = new RelayCommand(() => { }, () => false);
    }

    public event EventHandler? ReprintRequested;

    public ObservableCollection<HistorySourceOption> SourceOptions { get; } = [];

    public ObservableCollection<TerminalFilterOption> TerminalOptions { get; } = [];

    public ObservableCollection<HistoryOrderListItem> Orders { get; } = [];

    public ObservableCollection<ReceiptPreviewLine> ReceiptLines { get; } = [];

    public ObservableCollection<ReceiptPaymentLine> Payments { get; } = [];

    public IAsyncRelayCommand LoadCommand { get; }

    public IRelayCommand ReturnToPosCommand { get; }

    public IAsyncRelayCommand RecallSelectedCommand { get; }

    public IAsyncRelayCommand<HistoryOrderListItem> RecallOrderCommand { get; }

    public IRelayCommand ReprintCommand { get; }

    public IRelayCommand RefundCommand { get; }

    public TransactionHistorySource SelectedSource => SelectedSourceOption?.Source ?? TransactionHistorySource.LocalOrders;

    public bool IsRecallVisible => SelectedOrder?.CanRecall == true;

    public bool IsReprintVisible => CanReprintSelected();

    public bool IsLocalSourceSelected
    {
        get => SelectedSource == TransactionHistorySource.LocalOrders;
        set
        {
            if (value)
            {
                SetSelectedSource(TransactionHistorySource.LocalOrders);
            }
        }
    }

    public bool IsOnlineSourceSelected
    {
        get => SelectedSource == TransactionHistorySource.RemoteOrders;
        set
        {
            if (value)
            {
                SetSelectedSource(TransactionHistorySource.RemoteOrders);
            }
        }
    }

    public string TitleText => "TransactionHistory";

    public string SearchHintText => "订单号、货号、条码";

    public string ReceiptPreviewLabel => "success.receiptPreview";

    public string ReprintLabel => "history.reprint";

    public string RefundLabel => "history.refund";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        StatusMessage = string.Empty;
        try
        {
            var orders = SelectedSource switch
            {
                TransactionHistorySource.RemoteOrders => await LoadRemoteOrdersAsync(cancellationToken),
                _ => await LoadLocalAndSuspendedOrdersAsync(cancellationToken)
            };

            Orders.ReplaceWith(orders);
            _suppressSelectedOrderLoad = true;
            SelectedOrder = Orders.FirstOrDefault();
            _suppressSelectedOrderLoad = false;

            if (SelectedOrder is null)
            {
                ClearReceiptPreview();
                return;
            }

            await LoadSelectedReceiptAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Orders.Clear();
            ClearReceiptPreview();
            StatusMessage = ex.Message;
        }
    }

    public Task ShowSuspendedOrdersAsync(CancellationToken cancellationToken = default)
    {
        _suppressSourceAutoLoad = true;
        SelectedSourceOption = SourceOptions.First(x => x.Source == TransactionHistorySource.LocalOrders);
        _suppressSourceAutoLoad = false;
        return LoadAsync(cancellationToken);
    }

    partial void OnSelectedSourceOptionChanged(HistorySourceOption? value)
    {
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(IsRecallVisible));
        OnPropertyChanged(nameof(IsReprintVisible));
        OnPropertyChanged(nameof(IsLocalSourceSelected));
        OnPropertyChanged(nameof(IsOnlineSourceSelected));
        ReprintCommand.NotifyCanExecuteChanged();
        RecallSelectedCommand.NotifyCanExecuteChanged();
        RecallOrderCommand.NotifyCanExecuteChanged();
        if (!_suppressSourceAutoLoad)
        {
            _ = LoadAsync(CancellationToken.None);
        }
    }

    partial void OnSelectedOrderChanged(HistoryOrderListItem? value)
    {
        ReprintCommand.NotifyCanExecuteChanged();
        RecallSelectedCommand.NotifyCanExecuteChanged();
        RecallOrderCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsRecallVisible));
        OnPropertyChanged(nameof(IsReprintVisible));

        if (_suppressSelectedOrderLoad)
        {
            return;
        }

        _ = LoadSelectedReceiptAsync(CancellationToken.None);
    }

    partial void OnSessionChanged(PosSessionState value)
    {
        StoreFilterText = $"{value.StoreName} ({value.StoreCode})";
        RefreshTerminalOptions(SelectedTerminalOption?.DeviceCode is null);
    }

    partial void OnSelectedTerminalOptionChanged(TerminalFilterOption? value)
    {
        TerminalFilterText = value?.DeviceCode ?? "All Terminals";
    }

    private async Task<IReadOnlyList<HistoryOrderListItem>> LoadLocalAndSuspendedOrdersAsync(CancellationToken cancellationToken)
    {
        var localOrdersTask = LoadLocalOrdersAsync(cancellationToken);
        var suspendedOrdersTask = LoadSuspendedOrdersAsync(cancellationToken);
        await Task.WhenAll(localOrdersTask, suspendedOrdersTask);

        return localOrdersTask.Result
            .Concat(suspendedOrdersTask.Result)
            .OrderByDescending(order => order.OccurredAt)
            .ToList();
    }

    private async Task<IReadOnlyList<HistoryOrderListItem>> LoadLocalOrdersAsync(CancellationToken cancellationToken)
    {
        if (_receiptQueryService is null)
        {
            return [];
        }

        var query = new LocalOrderHistoryQuery(
            ParseDateFrom(DateFrom),
            ParseDateTo(DateTo),
            SelectedTerminalDeviceCode,
            NormalizeKeyword(SearchText));
        var orders = await _receiptQueryService.GetRecentOrdersAsync(query, 100, cancellationToken);
        return orders
            .Select(order => new HistoryOrderListItem(
                order.OrderGuid,
                TransactionHistorySource.LocalOrders,
                order.StoreCode,
                order.DeviceCode,
                order.CashierName,
                order.SoldAt,
                order.TotalAmount,
                order.DiscountAmount,
                order.ActualAmount,
                order.LineCount,
                order.PaymentSummary,
                order.StatusLabel))
            .ToList();
    }

    private async Task<IReadOnlyList<HistoryOrderListItem>> LoadSuspendedOrdersAsync(CancellationToken cancellationToken)
    {
        if (_suspendedOrderService is null)
        {
            return [];
        }

        var orders = await _suspendedOrderService.GetPendingOrdersAsync(
            Session.StoreCode,
            SelectedTerminalDeviceCode,
            NormalizeKeyword(SearchText),
            100,
            cancellationToken);
        var from = ParseDateFrom(DateFrom);
        var to = ParseDateTo(DateTo);
        return orders
            .Where(order => from is null || order.SuspendedAt >= from.Value)
            .Where(order => to is null || order.SuspendedAt <= to.Value)
            .Select(order => new HistoryOrderListItem(
                order.SuspendedOrderGuid,
                TransactionHistorySource.LocalOrders,
                order.StoreCode,
                order.DeviceCode,
                order.CashierName,
                order.SuspendedAt,
                order.TotalAmount,
                order.DiscountAmount,
                order.ActualAmount,
                order.LineCount,
                "挂单",
                "待取回",
                IsSuspendedOrder: true,
                CanRecall: true))
            .ToList();
    }

    private async Task<IReadOnlyList<HistoryOrderListItem>> LoadRemoteOrdersAsync(CancellationToken cancellationToken)
    {
        if (_remoteOrderHistoryService is null)
        {
            return [];
        }

        var result = await _remoteOrderHistoryService.QueryAsync(
            new RemoteOrderHistoryQuery(
                Session.StoreCode,
                ParseDateFrom(DateFrom),
                ParseDateTo(DateTo),
                SelectedTerminalDeviceCode,
                NormalizeKeyword(SearchText),
                100),
            cancellationToken);
        return result.Orders.Select(order => new HistoryOrderListItem(
            order.OrderGuid,
            TransactionHistorySource.RemoteOrders,
            order.StoreCode,
            order.DeviceCode,
            order.CashierName,
            order.SoldAt,
            order.TotalAmount,
            order.DiscountAmount,
            order.ActualAmount,
            order.LineCount,
            order.PaymentSummary,
            order.StatusLabel)).ToList();
    }

    private async Task LoadSelectedReceiptAsync(CancellationToken cancellationToken)
    {
        if (SelectedOrder is null)
        {
            ClearReceiptPreview();
            return;
        }

        ReceiptDetails? receipt = SelectedOrder.IsSuspendedOrder
            ? await GetSuspendedReceiptAsync(SelectedOrder.OrderGuid, cancellationToken)
            : SelectedOrder.Source switch
            {
                TransactionHistorySource.RemoteOrders => _remoteOrderHistoryService is null
                    ? null
                    : await _remoteOrderHistoryService.GetDetailsAsync(SelectedOrder.OrderGuid, cancellationToken),
                _ => _receiptQueryService is null ? null : await _receiptQueryService.GetReceiptAsync(SelectedOrder.OrderGuid, cancellationToken)
            };

        if (receipt is null)
        {
            ClearReceiptPreview();
            return;
        }

        ReceiptLines.ReplaceWith(receipt.Lines);
        Payments.ReplaceWith(receipt.Payments);
        PreviewSubtotal = receipt.TotalAmount;
        PreviewDiscount = receipt.DiscountAmount;
        PreviewTotal = receipt.ActualAmount;
        PreviewOrderId = receipt.TransactionIdDisplay;
        PreviewSoldAt = receipt.SoldAtDisplay;
    }

    private async Task<ReceiptDetails?> GetSuspendedReceiptAsync(Guid orderGuid, CancellationToken cancellationToken)
    {
        if (_suspendedOrderService is null)
        {
            return null;
        }

        var details = await _suspendedOrderService.GetOrderAsync(orderGuid, cancellationToken);
        return details is null ? null : CreateSuspendedReceipt(details);
    }

    private bool CanRecallSelected()
    {
        return CanRecallOrder(SelectedOrder);
    }

    private bool CanRecallOrder(HistoryOrderListItem? order)
    {
        return order?.CanRecall == true;
    }

    private bool CanReprintSelected()
    {
        return SelectedOrder is { IsSuspendedOrder: false, Source: TransactionHistorySource.LocalOrders };
    }

    private async Task RecallSelectedAsync()
    {
        await RecallOrderAsync(SelectedOrder);
    }

    private async Task RecallOrderAsync(HistoryOrderListItem? order)
    {
        if (!CanRecallOrder(order) || _suspendedOrderService is null)
        {
            return;
        }

        try
        {
            await _suspendedOrderService.RecallOrderAsync(order!.OrderGuid);
            if (_onSuspendedOrderRecalledAsync is not null)
            {
                await _onSuspendedOrderRecalledAsync();
            }

            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private static ReceiptDetails CreateSuspendedReceipt(SuspendedOrder order)
    {
        return new ReceiptDetails(
            order.SuspendedOrderGuid,
            order.StoreCode,
            order.DeviceCode,
            order.CashierName,
            order.SuspendedAt,
            order.TotalAmount,
            order.DiscountAmount,
            order.ActualAmount,
            order.Lines.Select(line => new ReceiptPreviewLine(
                line.DisplayName,
                line.LookupCode,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.ActualAmount)).ToList(),
            []);
    }

    private void ReturnToPos()
    {
        _returnToPos?.Invoke();
    }

    private bool CanReturnToPos()
    {
        return _returnToPos is not null;
    }

    private void ClearReceiptPreview()
    {
        ReceiptLines.Clear();
        Payments.Clear();
        PreviewSubtotal = 0m;
        PreviewDiscount = 0m;
        PreviewTotal = 0m;
        PreviewOrderId = "-";
        PreviewSoldAt = "-";
    }

    private static string? NormalizeKeyword(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private string? SelectedTerminalDeviceCode => SelectedTerminalOption?.DeviceCode;

    private void SetSelectedSource(TransactionHistorySource source)
    {
        if (SelectedSource == source)
        {
            return;
        }

        SelectedSourceOption = SourceOptions.First(option => option.Source == source);
    }

    private void RefreshTerminalOptions(bool selectAllTerminals)
    {
        var currentDeviceCode = Session.DeviceCode.Trim();
        TerminalOptions.Clear();
        var allTerminals = new TerminalFilterOption(null, "All Terminals");
        TerminalOptions.Add(allTerminals);

        TerminalFilterOption selected = allTerminals;
        if (!string.IsNullOrWhiteSpace(currentDeviceCode))
        {
            var currentTerminal = new TerminalFilterOption(currentDeviceCode, currentDeviceCode);
            TerminalOptions.Add(currentTerminal);
            selected = selectAllTerminals ? allTerminals : currentTerminal;
        }

        SelectedTerminalOption = selected;
        TerminalFilterText = selected.DeviceCode ?? "All Terminals";
    }

    private static DateTimeOffset? ParseDateFrom(DateTime? value)
    {
        return value is null ? null : new DateTimeOffset(value.Value.Date);
    }

    private static DateTimeOffset? ParseDateTo(DateTime? value)
    {
        return value is null ? null : new DateTimeOffset(value.Value.Date.AddDays(1).AddTicks(-1));
    }
}
