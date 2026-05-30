using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class InstallmentCenterViewModel : ObservableObject
{
    private readonly IInstallmentOrderService _installmentOrderService;
    private readonly Func<PosCartServiceSnapshot?, Task> _showCreateAsync;
    private readonly Action _backToPayment;
    private readonly ILocalizationService? _localization;
    private readonly ICardTerminalClient? _cardTerminalClient;

    [ObservableProperty] private PosSessionState _session;
    [ObservableProperty] private PosCartServiceSnapshot? _cartSnapshot;
    [ObservableProperty] private InstallmentOrderSummary? _selectedOrder;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private decimal _repaymentAmount;
    [ObservableProperty] private PaymentMethodKind _repaymentMethod = PaymentMethodKind.Cash;
    [ObservableProperty] private string _repaymentReference = string.Empty;
    [ObservableProperty] private string _repaymentVoucherToken = string.Empty;
    [ObservableProperty] private string _voidReason = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public InstallmentCenterViewModel(
        IInstallmentOrderService installmentOrderService,
        PosSessionState session,
        Func<PosCartServiceSnapshot?, Task> showCreateAsync,
        Action backToPayment,
        ILocalizationService? localization = null,
        ICardTerminalClient? cardTerminalClient = null)
    {
        _installmentOrderService = installmentOrderService;
        _session = session;
        _showCreateAsync = showCreateAsync;
        _backToPayment = backToPayment;
        _localization = localization;
        _cardTerminalClient = cardTerminalClient;
        if (_localization is not null)
        {
            _localization.CultureChanged += (_, _) => RaiseLocalizedProperties();
        }

        LoadCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsBusy);
        CreateInstallmentCommand = new AsyncRelayCommand(CreateInstallmentAsync, CanCreateInstallment);
        AddRepaymentCommand = new AsyncRelayCommand(AddRepaymentAsync, CanAddRepayment);
        CancelWithRefundCommand = new AsyncRelayCommand(CancelWithRefundAsync, CanCancelWithRefund);
        VoidCancelCommand = new AsyncRelayCommand(VoidCancelAsync, CanVoidCancel);
        ConfirmPickupCommand = new AsyncRelayCommand(ConfirmPickupAsync, CanConfirmPickup);
        BackToPaymentCommand = new RelayCommand(_backToPayment);
        StatusMessage = T("installment.center.status.ready", "请选择要创建或处理的分期单。");
    }

    public ObservableCollection<InstallmentOrderSummary> Orders { get; } = [];
    public IReadOnlyList<InstallmentPaymentMethodOption> PaymentMethodOptions { get; } =
    [
        new InstallmentPaymentMethodOption(PaymentMethodKind.Cash, "现金"),
        new InstallmentPaymentMethodOption(PaymentMethodKind.Card, "银行卡"),
        new InstallmentPaymentMethodOption(PaymentMethodKind.Voucher, "代金券")
    ];

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand CreateInstallmentCommand { get; }
    public IAsyncRelayCommand AddRepaymentCommand { get; }
    public IAsyncRelayCommand CancelWithRefundCommand { get; }
    public IAsyncRelayCommand VoidCancelCommand { get; }
    public IAsyncRelayCommand ConfirmPickupCommand { get; }
    public IRelayCommand BackToPaymentCommand { get; }

    public string PageTitleText => T("installment.center.title", "分期中心");
    public string CurrentOrderSummaryText => CartSnapshot is null ? "当前没有待创建的订单。" : string.Format(GetCulture(), "当前订单金额 {0:C2}，可发起新的分期单。", CartSnapshot.ActualAmount);
    public string CreateInstallmentText => T("installment.center.action.create", "创建分期");
    public string AddRepaymentText => T("installment.center.action.repay", "补款");
    public string CancelWithRefundText => T("installment.center.action.cancel", "取消退款");
    public string VoidCancelText => T("installment.center.action.void", "作废");
    public string ConfirmPickupText => T("installment.center.action.confirmPickup", "确认提货");
    public string LoadText => T("common.load", "加载");
    public string SearchTextLabel => T("installment.center.search", "搜索单号、姓名、电话");
    public string BackToPaymentText => T("installment.center.action.backToPayment", "返回付款");
    public string OfflineNoticeText => T("installment.center.offline", "离线时仅可查看本机缓存和打印已有凭证。");
    public bool IsOffline => !Session.IsOnline;
    public bool HasOrders => Orders.Count > 0;
    public bool IsCreateEnabled => CanCreateInstallment();
    public bool IsAddRepaymentEnabled => CanAddRepayment();
    public bool IsCancelWithRefundEnabled => CanCancelWithRefund();
    public bool IsVoidCancelEnabled => CanVoidCancel();
    public bool IsConfirmPickupEnabled => CanConfirmPickup();

    partial void OnSelectedOrderChanged(InstallmentOrderSummary? value)
    {
        RepaymentAmount = value?.OutstandingAmount ?? 0m;
        RaiseSelectionStateChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        LoadCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        RaiseSelectionStateChanged();
    }

    partial void OnRepaymentAmountChanged(decimal value) => RaiseSelectionStateChanged();
    partial void OnRepaymentMethodChanged(PaymentMethodKind value) => RaiseSelectionStateChanged();
    partial void OnRepaymentReferenceChanged(string value) => RaiseSelectionStateChanged();
    partial void OnRepaymentVoucherTokenChanged(string value) => RaiseSelectionStateChanged();

    public async Task LoadAsync() => await LoadCoreAsync(() => _installmentOrderService.GetOrdersAsync(Session), "已加载 {0} 条分期单。");
    public async Task SearchAsync() => await LoadCoreAsync(() => _installmentOrderService.SearchAsync(Session, SearchText), "已找到 {0} 条分期单。");

    public void Prepare(PosSessionState session, PosCartServiceSnapshot? cartSnapshot)
    {
        Session = session;
        CartSnapshot = cartSnapshot;
        OnPropertyChanged(nameof(CurrentOrderSummaryText));
        RaiseSelectionStateChanged();
    }

    public void AppendOrUpdateOrder(InstallmentOrderSummary order)
    {
        var existing = Orders.FirstOrDefault(item => item.OrderId == order.OrderId);
        if (existing is null)
        {
            Orders.Insert(0, order);
        }
        else
        {
            Orders[Orders.IndexOf(existing)] = order;
        }
        SelectedOrder = order;
        OnPropertyChanged(nameof(HasOrders));
    }

    private async Task<bool> LoadCoreAsync(
        Func<Task<IReadOnlyList<InstallmentOrderSummary>>> loader,
        string loadedFormat,
        string? actionMessage = null)
    {
        IsBusy = true;
        try
        {
            var orders = await loader();
            Orders.ReplaceWith(orders);
            SelectedOrder = Orders.FirstOrDefault();
            StatusMessage = actionMessage ?? (orders.Count == 0 ? "当前没有分期单。" : string.Format(GetCulture(), loadedFormat, orders.Count));
            OnPropertyChanged(nameof(HasOrders));
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = actionMessage is null ? ex.Message : $"{actionMessage}（刷新失败：{ex.Message}）";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task CreateInstallmentAsync() => _showCreateAsync(CartSnapshot);
    private bool CanCreateInstallment() => !IsBusy && !IsOffline && CartSnapshot is { ActualAmount: > 0m };

    private async Task AddRepaymentAsync()
    {
        if (SelectedOrder is null) return;

        var payment = new InstallmentPaymentDraft(
            Guid.NewGuid(),
            RepaymentMethod,
            RepaymentAmount,
            Normalize(RepaymentReference),
            Normalize(RepaymentVoucherToken));
        if (RepaymentMethod == PaymentMethodKind.Card)
        {
            if (_cardTerminalClient is null)
            {
                StatusMessage = "银行卡补款需要先配置刷卡终端。";
                return;
            }

            // 刷卡补款必须先由终端授权，API 只记录已授权结果。
            var authorization = await _cardTerminalClient.AuthorizeAsync(RepaymentAmount, Session);
            if (!authorization.Approved)
            {
                StatusMessage = authorization.Message ?? "银行卡补款未授权。";
                return;
            }

            payment = payment with
            {
                Amount = authorization.AuthorizedAmount ?? RepaymentAmount,
                Reference = authorization.Reference ?? Normalize(RepaymentReference),
                CardTransactions = authorization.CardTransactions
            };
        }

        await RunOrderActionAsync(() => _installmentOrderService.AddRepaymentAsync(new InstallmentOrderRepaymentRequest(SelectedOrder.OrderId, Session, payment)));
    }

    private bool CanAddRepayment() => !IsBusy &&
        !IsOffline &&
        SelectedOrder is { CanAddRepayment: true } &&
        RepaymentAmount > 0m &&
        RepaymentAmount <= SelectedOrder.OutstandingAmount &&
        (RepaymentMethod != PaymentMethodKind.Card || _cardTerminalClient is not null) &&
        (RepaymentMethod != PaymentMethodKind.Voucher || (!string.IsNullOrWhiteSpace(RepaymentReference) && !string.IsNullOrWhiteSpace(RepaymentVoucherToken)));
    private Task CancelWithRefundAsync() => SelectedOrder is null ? Task.CompletedTask : RunOrderActionAsync(() => _installmentOrderService.CancelWithRefundAsync(SelectedOrder.OrderId, Session));
    private bool CanCancelWithRefund() => !IsBusy && !IsOffline && SelectedOrder is { CanCancelWithRefund: true };
    private Task VoidCancelAsync() => SelectedOrder is null ? Task.CompletedTask : RunOrderActionAsync(() => _installmentOrderService.VoidCancelAsync(SelectedOrder.OrderId, Session, VoidReason));
    private bool CanVoidCancel() => !IsBusy && !IsOffline && SelectedOrder is { CanVoidCancel: true };
    private Task ConfirmPickupAsync() => SelectedOrder is null ? Task.CompletedTask : RunOrderActionAsync(() => _installmentOrderService.ConfirmPickupAsync(SelectedOrder.OrderId, Session));
    private bool CanConfirmPickup() => !IsBusy && !IsOffline && SelectedOrder is { CanConfirmPickup: true };

    private async Task RunOrderActionAsync(Func<Task<InstallmentOrderActionResult>> action)
    {
        IsBusy = true;
        try
        {
            var result = await action();
            StatusMessage = result.Message;
            if (result.Succeeded)
            {
                await LoadCoreAsync(() => _installmentOrderService.SearchAsync(Session, SearchText), "已找到 {0} 条分期单。", result.Message);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseSelectionStateChanged()
    {
        CreateInstallmentCommand.NotifyCanExecuteChanged();
        AddRepaymentCommand.NotifyCanExecuteChanged();
        CancelWithRefundCommand.NotifyCanExecuteChanged();
        VoidCancelCommand.NotifyCanExecuteChanged();
        ConfirmPickupCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsCreateEnabled));
        OnPropertyChanged(nameof(IsAddRepaymentEnabled));
        OnPropertyChanged(nameof(IsCancelWithRefundEnabled));
        OnPropertyChanged(nameof(IsVoidCancelEnabled));
        OnPropertyChanged(nameof(IsConfirmPickupEnabled));
        OnPropertyChanged(nameof(IsOffline));
    }

    private void RaiseLocalizedProperties()
    {
        OnPropertyChanged(nameof(PageTitleText));
        OnPropertyChanged(nameof(CreateInstallmentText));
        OnPropertyChanged(nameof(AddRepaymentText));
        OnPropertyChanged(nameof(CancelWithRefundText));
        OnPropertyChanged(nameof(VoidCancelText));
        OnPropertyChanged(nameof(ConfirmPickupText));
    }

    private string T(string key, string fallback) => string.IsNullOrWhiteSpace(_localization?.T(key)) || _localization?.T(key) == key ? fallback : _localization!.T(key);
    private IFormatProvider GetCulture() => _localization?.CurrentCulture ?? System.Globalization.CultureInfo.CurrentCulture;
    private static string? Normalize(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
