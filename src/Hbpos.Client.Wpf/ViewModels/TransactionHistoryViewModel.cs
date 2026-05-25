using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class TransactionHistoryViewModel : ObservableObject
{
    private readonly IReceiptQueryService? _receiptQueryService;
    private bool _suppressSelectedOrderLoad;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _dateFilterText = "Today";

    [ObservableProperty]
    private string _storeFilterText = "All Stores";

    [ObservableProperty]
    private string _terminalFilterText = "All Terminals";

    [ObservableProperty]
    private LocalOrderSummary? _selectedOrder;

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

    public TransactionHistoryViewModel()
        : this(initialize: true, receiptQueryService: null)
    {
    }

    public TransactionHistoryViewModel(ILocalOrderRepository orderRepository)
        : this(initialize: true, receiptQueryService: new ReceiptQueryService(orderRepository))
    {
    }

    public TransactionHistoryViewModel(IReceiptQueryService receiptQueryService)
        : this(initialize: true, receiptQueryService)
    {
    }

    private TransactionHistoryViewModel(bool initialize, IReceiptQueryService? receiptQueryService)
    {
        _receiptQueryService = receiptQueryService;
        LoadCommand = new AsyncRelayCommand(() => LoadAsync());
        ReprintCommand = new RelayCommand(() => ReprintRequested?.Invoke(this, EventArgs.Empty), () => SelectedOrder is not null);
        RefundCommand = new RelayCommand(() => { }, () => false);
    }

    public event EventHandler? ReprintRequested;

    public ObservableCollection<LocalOrderSummary> Orders { get; } = [];

    public ObservableCollection<ReceiptPreviewLine> ReceiptLines { get; } = [];

    public ObservableCollection<ReceiptPaymentLine> Payments { get; } = [];

    public IAsyncRelayCommand LoadCommand { get; }

    public IRelayCommand ReprintCommand { get; }

    public IRelayCommand RefundCommand { get; }

    public string TitleText => "TransactionHistory";

    public string SearchHintText => "history.search";

    public string ReceiptPreviewLabel => "success.receiptPreview";

    public string ReprintLabel => "history.reprint";

    public string RefundLabel => "history.refund";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_receiptQueryService is null)
        {
            return;
        }

        var orders = await _receiptQueryService.GetRecentOrdersAsync(50, cancellationToken);
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

    partial void OnSelectedOrderChanged(LocalOrderSummary? value)
    {
        ReprintCommand.NotifyCanExecuteChanged();

        if (_suppressSelectedOrderLoad)
        {
            return;
        }

        _ = LoadSelectedReceiptAsync(CancellationToken.None);
    }

    private async Task LoadSelectedReceiptAsync(CancellationToken cancellationToken)
    {
        if (_receiptQueryService is null || SelectedOrder is null)
        {
            ClearReceiptPreview();
            return;
        }

        var receipt = await _receiptQueryService.GetReceiptAsync(SelectedOrder.OrderGuid, cancellationToken);
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
}
