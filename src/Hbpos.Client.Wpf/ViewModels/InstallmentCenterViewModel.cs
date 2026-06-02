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
    private string? _statusResourceKey;
    private string _statusFallback = string.Empty;
    private object[] _statusResourceArgs = [];

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

        RefreshPaymentMethodOptions();
        SetStatusResource("installment.center.status.ready", "Select an installment order to create or process.");
    }

    public ObservableCollection<InstallmentOrderSummary> Orders { get; } = [];
    public ObservableCollection<InstallmentPaymentMethodOption> PaymentMethodOptions { get; } = [];

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand CreateInstallmentCommand { get; }
    public IAsyncRelayCommand AddRepaymentCommand { get; }
    public IAsyncRelayCommand CancelWithRefundCommand { get; }
    public IAsyncRelayCommand VoidCancelCommand { get; }
    public IAsyncRelayCommand ConfirmPickupCommand { get; }
    public IRelayCommand BackToPaymentCommand { get; }

    public string PageTitleText => T("installment.center.title", "Installment Center");
    public string CurrentOrderSummaryText => CartSnapshot is null
        ? T("installment.center.currentOrder.none", "There is no current order available for a new installment.")
        : string.Format(GetCulture(), T("installment.center.currentOrder.amount", "Current order amount {0:C2}. A new installment can be created."), CartSnapshot.ActualAmount);
    public string CreateInstallmentText => T("installment.center.action.create", "Create Installment");
    public string AddRepaymentText => T("installment.center.action.repay", "Add Repayment");
    public string CancelWithRefundText => T("installment.center.action.cancel", "Cancel and Refund");
    public string VoidCancelText => T("installment.center.action.void", "Void");
    public string ConfirmPickupText => T("installment.center.action.confirmPickup", "Confirm Pickup");
    public string LoadText => T("common.load", "Load");
    public string SearchButtonText => T("installment.center.action.search", "Search");
    public string SearchTextLabel => T("installment.center.search", "Search order no., name, or phone");
    public string BackToPaymentText => T("installment.center.action.backToPayment", "Back to Payment");
    public string OfflineNoticeText => T("installment.center.offline", "Offline mode only supports local cached installment orders.");
    public string SelectedOrderNumberText => SelectedOrder?.OrderNumber ?? T("installment.center.selected.none", "No installment selected");
    public string SelectedOrderCustomerText => SelectedOrder?.CustomerName ?? T("installment.center.selected.customer.empty", "Select an installment order on the left");
    public string SelectedOrderOutstandingText => SelectedOrder is null
        ? T("installment.center.selected.outstanding.empty", "Outstanding -")
        : string.Format(GetCulture(), T("installment.center.selected.outstanding", "Outstanding {0:C2}"), SelectedOrder.OutstandingAmount);
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
        OnPropertyChanged(nameof(SelectedOrderNumberText));
        OnPropertyChanged(nameof(SelectedOrderCustomerText));
        OnPropertyChanged(nameof(SelectedOrderOutstandingText));
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

    public async Task LoadAsync() => await LoadCoreAsync(() => _installmentOrderService.GetOrdersAsync(Session), "installment.center.status.loaded", "Loaded {0} installment orders.");
    public async Task SearchAsync() => await LoadCoreAsync(() => _installmentOrderService.SearchAsync(Session, SearchText), "installment.center.status.searched", "Found {0} installment orders.");

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
        string loadedFormatKey,
        string loadedFormatFallback,
        string? actionMessage = null)
    {
        IsBusy = true;
        try
        {
            var orders = await loader();
            Orders.ReplaceWith(orders);
            SelectedOrder = Orders.FirstOrDefault();
            if (actionMessage is not null)
            {
                SetLiteralStatus(actionMessage);
            }
            else if (orders.Count == 0)
            {
                SetStatusResource("installment.center.status.empty", "There are no installment orders.");
            }
            else
            {
                SetStatusResource(loadedFormatKey, loadedFormatFallback, orders.Count);
            }

            OnPropertyChanged(nameof(HasOrders));
            return true;
        }
        catch (Exception ex)
        {
            if (actionMessage is null)
            {
                SetLiteralStatus(ex.Message);
            }
            else
            {
                SetLiteralStatus(string.Format(GetCulture(), T("installment.center.status.refreshFailed", "{0} (refresh failed: {1})"), actionMessage, ex.Message));
            }

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
                SetStatusResource("installment.center.status.cardTerminalRequired", "Configure a card terminal before adding a card repayment.");
                return;
            }

            // 银行卡补款必须先由终端授权，API 只记录已授权的付款结果。
            var authorization = await _cardTerminalClient.AuthorizeAsync(RepaymentAmount, Session);
            if (!authorization.Approved)
            {
                SetLiteralStatus(authorization.Message ?? T("installment.center.status.cardNotAuthorized", "Card repayment was not authorized."));
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
            SetLiteralStatus(result.Message);
            if (result.Succeeded)
            {
                await LoadCoreAsync(() => _installmentOrderService.SearchAsync(Session, SearchText), "installment.center.status.searched", "Found {0} installment orders.", result.Message);
            }
        }
        catch (Exception ex)
        {
            SetLiteralStatus(ex.Message);
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
        RefreshPaymentMethodOptions();
        if (_statusResourceKey is not null)
        {
            StatusMessage = FormatResource(_statusResourceKey, _statusFallback, _statusResourceArgs);
        }

        OnPropertyChanged(nameof(PageTitleText));
        OnPropertyChanged(nameof(CurrentOrderSummaryText));
        OnPropertyChanged(nameof(CreateInstallmentText));
        OnPropertyChanged(nameof(AddRepaymentText));
        OnPropertyChanged(nameof(CancelWithRefundText));
        OnPropertyChanged(nameof(VoidCancelText));
        OnPropertyChanged(nameof(ConfirmPickupText));
        OnPropertyChanged(nameof(LoadText));
        OnPropertyChanged(nameof(SearchButtonText));
        OnPropertyChanged(nameof(SearchTextLabel));
        OnPropertyChanged(nameof(BackToPaymentText));
        OnPropertyChanged(nameof(OfflineNoticeText));
        OnPropertyChanged(nameof(SelectedOrderNumberText));
        OnPropertyChanged(nameof(SelectedOrderCustomerText));
        OnPropertyChanged(nameof(SelectedOrderOutstandingText));
    }

    private void RefreshPaymentMethodOptions()
    {
        PaymentMethodOptions.Clear();
        PaymentMethodOptions.Add(new InstallmentPaymentMethodOption(PaymentMethodKind.Cash, T("payment.method.cash", "Cash")));
        PaymentMethodOptions.Add(new InstallmentPaymentMethodOption(PaymentMethodKind.Card, T("payment.method.card", "Credit/Debit Card")));
        PaymentMethodOptions.Add(new InstallmentPaymentMethodOption(PaymentMethodKind.Voucher, T("payment.method.voucher", "Voucher")));
        OnPropertyChanged(nameof(PaymentMethodOptions));
    }

    private void SetStatusResource(string key, string fallback, params object[] args)
    {
        _statusResourceKey = key;
        _statusFallback = fallback;
        _statusResourceArgs = args;
        StatusMessage = FormatResource(key, fallback, args);
    }

    private void SetLiteralStatus(string value)
    {
        _statusResourceKey = null;
        _statusFallback = string.Empty;
        _statusResourceArgs = [];
        StatusMessage = value;
    }

    private string FormatResource(string key, string fallback, object[] args)
    {
        var format = T(key, fallback);
        return args.Length == 0 ? format : string.Format(GetCulture(), format, args);
    }

    private string T(string key, string fallback)
    {
        var value = _localization?.T(key);
        return IsMissingLocalizedValue(key, value) ? fallback : value!;
    }

    private static bool IsMissingLocalizedValue(string key, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ||
            value == key ||
            (value.StartsWith("[[", StringComparison.Ordinal) && value.EndsWith("]]", StringComparison.Ordinal));
    }

    private IFormatProvider GetCulture() => _localization?.CurrentCulture ?? System.Globalization.CultureInfo.CurrentCulture;
    private static string? Normalize(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
