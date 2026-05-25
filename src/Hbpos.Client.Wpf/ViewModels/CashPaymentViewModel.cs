using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class CashPaymentViewModel : ObservableObject
{
    private readonly PosCartService _cart;
    private readonly ICashPaymentWorkflowService _workflowService;
    private readonly ILocalizationService? _localization;
    private string _statusKey = "payment.cash.status.ready";

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private string _amountTenderedText = string.Empty;

    [ObservableProperty]
    private decimal _changeDue;

    [ObservableProperty]
    private int _pendingSyncCount;

    public CashPaymentViewModel(
        PosCartService cart,
        CashCheckoutService checkout,
        ILocalOrderRepository orderRepository,
        ISyncQueueRepository syncQueueRepository,
        PosSessionState session,
        ILocalizationService? localization = null)
        : this(
            cart,
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueueRepository),
            session,
            localization)
    {
    }

    public CashPaymentViewModel(
        PosCartService cart,
        ICashPaymentWorkflowService workflowService,
        PosSessionState session,
        ILocalizationService? localization = null)
    {
        _cart = cart;
        _workflowService = workflowService;
        _session = session;
        _localization = localization;
        _amountTenderedText = cart.ActualAmount > 0 ? cart.ActualAmount.ToString("0.00") : string.Empty;
        if (_localization is not null)
        {
            _localization.CultureChanged += (_, _) => RaiseLocalizedProperties();
        }

        NumberInputCommand = new RelayCommand<string>(AppendAmountTendered);
        QuickCashCommand = new RelayCommand<decimal>(ApplyQuickCash);
        ConfirmPaymentCommand = new AsyncRelayCommand(ConfirmPaymentAsync, CanConfirmPayment);
        CancelCommand = new RelayCommand(() => PaymentCancelled?.Invoke(this, EventArgs.Empty));

        PaymentMethods =
        [
            new PaymentMethodOption(PaymentMethodKind.Cash, "payment.method.cash", true, true),
            new PaymentMethodOption(PaymentMethodKind.Card, "payment.method.card", false, false),
            new PaymentMethodOption(null, "payment.method.account", false, false),
            new PaymentMethodOption(null, "payment.method.installment", false, false)
        ];

        RefreshCart();
        RecalculateChange();
    }

    public ObservableCollection<CartLine> CartLines { get; } = [];

    public IReadOnlyList<PaymentMethodOption> PaymentMethods { get; }

    public IReadOnlyList<decimal> QuickCashAmounts => BuildQuickCashAmounts();

    public IRelayCommand<string> NumberInputCommand { get; }

    public IRelayCommand<decimal> QuickCashCommand { get; }

    public IAsyncRelayCommand ConfirmPaymentCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public event EventHandler<PaymentCompletedEventArgs>? PaymentCompleted;

    public event EventHandler? PaymentCancelled;

    public string ScreenTitleText => T("payment.cash.title");

    public string OrderSummaryText => T("payment.cash.orderSummary");

    public string AmountTenderedTextLabel => T("payment.cash.amountTendered");

    public string ChangeDueText => T("payment.cash.changeDue");

    public string QuickCashText => T("payment.cash.quickCash");

    public string PaymentMethodText => T("payment.cash.paymentMethod");

    public string ConfirmPaymentText => T("payment.cash.confirm");

    public string CancelText => T("common.cancel");

    public string StatusMessage => T(_statusKey);

    public decimal TotalAmount => _cart.TotalAmount;

    public decimal DiscountAmount => _cart.DiscountAmount;

    public decimal ActualAmount => _cart.ActualAmount;

    partial void OnAmountTenderedTextChanged(string value)
    {
        RecalculateChange();
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
    }

    partial void OnSessionChanged(PosSessionState value)
    {
        PendingSyncCount = value.PendingSyncCount;
    }

    public void RefreshCart()
    {
        CartLines.ReplaceWith(_cart.Lines);
        OnPropertyChanged(nameof(TotalAmount));
        OnPropertyChanged(nameof(DiscountAmount));
        OnPropertyChanged(nameof(ActualAmount));
        OnPropertyChanged(nameof(QuickCashAmounts));
        RecalculateChange();
        RefreshCartValidationStatus();
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
    }

    private void AppendAmountTendered(string? value)
    {
        if (value == "Back")
        {
            AmountTenderedText = AmountTenderedText.Length > 0 ? AmountTenderedText[..^1] : string.Empty;
            return;
        }

        if (value == "Clear")
        {
            AmountTenderedText = string.Empty;
            return;
        }

        if (value == "." && AmountTenderedText.Contains('.', StringComparison.Ordinal))
        {
            return;
        }

        AmountTenderedText += value;
    }

    private void ApplyQuickCash(decimal amount)
    {
        AmountTenderedText = amount.ToString("0.00");
    }

    private async Task ConfirmPaymentAsync()
    {
        if (TrySetBlockingCartIssueStatus())
        {
            ConfirmPaymentCommand.NotifyCanExecuteChanged();
            return;
        }

        if (!_workflowService.TryParseTenderedAmount(AmountTenderedText, out _))
        {
            SetStatus("payment.cash.status.invalidTendered");
            return;
        }

        var result = await _workflowService.CompleteAsync(_cart, Session, AmountTenderedText);
        RefreshCart();
        PendingSyncCount = result.PendingSyncCount;
        Session = result.UpdatedSession;
        SetStatus("payment.cash.status.completed");
        PaymentCompleted?.Invoke(this, new PaymentCompletedEventArgs(result.Order, result.TenderedAmount, result.ChangeAmount));
    }

    private bool CanConfirmPayment()
    {
        return !_cart.IsEmpty &&
            !_cart.HasNonIntegerQuantity &&
            !_cart.HasReturnLine &&
            !_cart.HasZeroPriceLine &&
            _workflowService.TryParseTenderedAmount(AmountTenderedText, out var tendered) &&
            tendered >= ActualAmount;
    }

    private void RefreshCartValidationStatus()
    {
        if (TrySetBlockingCartIssueStatus())
        {
            return;
        }

        if (_statusKey is "cart.status.quantityMustBeInteger" or "cart.status.zeroPriceItem" or "payment.cash.status.returnCheckoutNotReady")
        {
            SetStatus("payment.cash.status.ready");
        }
    }

    private bool TrySetBlockingCartIssueStatus()
    {
        if (_cart.HasNonIntegerQuantity)
        {
            SetStatus("cart.status.quantityMustBeInteger");
            return true;
        }

        if (_cart.HasReturnLine)
        {
            SetStatus("payment.cash.status.returnCheckoutNotReady");
            return true;
        }

        if (_cart.HasZeroPriceLine)
        {
            SetStatus("cart.status.zeroPriceItem");
            return true;
        }

        return false;
    }

    private void RecalculateChange()
    {
        ChangeDue = _workflowService.CalculateChange(AmountTenderedText, ActualAmount);
    }

    private IReadOnlyList<decimal> BuildQuickCashAmounts()
    {
        if (ActualAmount <= 0)
        {
            return [5m, 10m, 20m, 50m];
        }

        var rounded = Math.Ceiling(ActualAmount);
        var amounts = new SortedSet<decimal>
        {
            ActualAmount,
            rounded,
            NextMultiple(ActualAmount, 5m),
            NextMultiple(ActualAmount, 10m),
            NextMultiple(ActualAmount, 20m),
            NextMultiple(ActualAmount, 50m)
        };

        return amounts.Where(amount => amount >= ActualAmount).Take(6).ToList();
    }

    private static decimal NextMultiple(decimal value, decimal multiple)
    {
        return Math.Ceiling(value / multiple) * multiple;
    }

    private void SetStatus(string key)
    {
        _statusKey = key;
        OnPropertyChanged(nameof(StatusMessage));
    }

    private string T(string key)
    {
        return _localization?.T(key) ?? key;
    }

    private void RaiseLocalizedProperties()
    {
        OnPropertyChanged(nameof(ScreenTitleText));
        OnPropertyChanged(nameof(OrderSummaryText));
        OnPropertyChanged(nameof(AmountTenderedTextLabel));
        OnPropertyChanged(nameof(ChangeDueText));
        OnPropertyChanged(nameof(QuickCashText));
        OnPropertyChanged(nameof(PaymentMethodText));
        OnPropertyChanged(nameof(ConfirmPaymentText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(StatusMessage));
    }
}

public sealed record PaymentMethodOption(PaymentMethodKind? Method, string Label, bool IsSelected, bool IsEnabled);
