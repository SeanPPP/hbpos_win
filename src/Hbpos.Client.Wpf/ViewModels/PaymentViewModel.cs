using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

public partial class PaymentViewModel : ObservableObject
{
    private readonly PosCartService _cart;
    private readonly ICashPaymentWorkflowService _workflowService;
    private readonly ILocalizationService? _localization;
    private string _statusKey = "payment.status.ready";
    private string? _statusTextOverride;
    private Guid? _pendingVoucherUploadOrderGuid;
    private decimal _pendingVoucherTenderedAmount;
    private decimal _pendingVoucherChangeAmount;

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private PaymentMethodKind _selectedPaymentMethod = PaymentMethodKind.Cash;

    [ObservableProperty]
    private string _tenderAmountText = string.Empty;

    [ObservableProperty]
    private string _voucherCodeText = string.Empty;

    [ObservableProperty]
    private decimal _changeDue;

    [ObservableProperty]
    private decimal _remainingAmount;

    [ObservableProperty]
    private decimal _totalTendered;

    [ObservableProperty]
    private int _pendingSyncCount;

    public PaymentViewModel(
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

    public PaymentViewModel(
        PosCartService cart,
        ICashPaymentWorkflowService workflowService,
        PosSessionState session,
        ILocalizationService? localization = null)
    {
        _cart = cart;
        _workflowService = workflowService;
        _session = session;
        _localization = localization;
        if (_localization is not null)
        {
            _localization.CultureChanged += (_, _) => RaiseLocalizedProperties();
        }

        NumberInputCommand = new RelayCommand<string>(AppendTenderAmount);
        QuickCashCommand = new RelayCommand<decimal>(ApplyQuickCash);
        SelectCashCommand = new RelayCommand(() => SelectedPaymentMethod = PaymentMethodKind.Cash);
        SelectCardCommand = new RelayCommand(() => SelectedPaymentMethod = PaymentMethodKind.Card);
        SelectVoucherCommand = new RelayCommand(() => SelectedPaymentMethod = PaymentMethodKind.Voucher);
        AddTenderCommand = new AsyncRelayCommand(AddTenderAsync, CanAddTender);
        RemoveTenderCommand = new RelayCommand<PaymentTender>(RemoveTender);
        ConfirmPaymentCommand = new AsyncRelayCommand(ConfirmPaymentAsync, CanConfirmPayment);
        CancelCommand = new RelayCommand(() => PaymentCancelled?.Invoke(this, EventArgs.Empty));

        RefreshCart();
    }

    public ObservableCollection<CartLine> CartLines { get; } = [];

    public ObservableCollection<PaymentTender> PaymentTenders { get; } = [];

    public IReadOnlyList<decimal> QuickCashAmounts => BuildQuickCashAmounts();

    public IRelayCommand<string> NumberInputCommand { get; }

    public IRelayCommand<decimal> QuickCashCommand { get; }

    public IRelayCommand SelectCashCommand { get; }

    public IRelayCommand SelectCardCommand { get; }

    public IRelayCommand SelectVoucherCommand { get; }

    public IAsyncRelayCommand AddTenderCommand { get; }

    public IRelayCommand<PaymentTender> RemoveTenderCommand { get; }

    public IAsyncRelayCommand ConfirmPaymentCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public event EventHandler<PaymentCompletedEventArgs>? PaymentCompleted;

    public event EventHandler? PaymentCancelled;

    public string ScreenTitleText => T("payment.title");

    public string OrderSummaryText => T("payment.orderSummary");

    public string CurrentTenderTextLabel => T("payment.currentTender");

    public string AmountTenderedTextLabel => CurrentTenderTextLabel;

    public string RemainingAmountText => T("payment.remaining");

    public string ChangeDueText => T("payment.changeDue");

    public string QuickCashText => T("payment.quickCash");

    public string PaymentMethodText => T("payment.method");

    public string AppliedTendersText => T("payment.appliedTenders");

    public string AddTenderText => T("payment.addTender");

    public string ConfirmPaymentText => T("payment.confirm");

    public string NoTendersText => T("payment.noTenders");

    public string CancelText => T("common.cancel");

    public string StatusMessage => _statusTextOverride ?? T(_statusKey);

    public decimal TotalAmount => _cart.TotalAmount;

    public decimal DiscountAmount => _cart.DiscountAmount;

    public decimal ActualAmount => _cart.ActualAmount;

    public string AmountTenderedText
    {
        get => TenderAmountText;
        set => TenderAmountText = value;
    }

    public bool IsCashSelected => SelectedPaymentMethod == PaymentMethodKind.Cash;

    public bool IsCardSelected => SelectedPaymentMethod == PaymentMethodKind.Card;

    public bool IsVoucherSelected => SelectedPaymentMethod == PaymentMethodKind.Voucher;

    partial void OnTenderAmountTextChanged(string value)
    {
        RecalculateTenderSummary();
        AddTenderCommand.NotifyCanExecuteChanged();
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
    }

    partial void OnVoucherCodeTextChanged(string value)
    {
        AddTenderCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPaymentMethodChanged(PaymentMethodKind value)
    {
        SyncTenderAmountToRemaining();
        AddTenderCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsCashSelected));
        OnPropertyChanged(nameof(IsCardSelected));
        OnPropertyChanged(nameof(IsVoucherSelected));
        OnPropertyChanged(nameof(QuickCashAmounts));
    }

    partial void OnSessionChanged(PosSessionState value)
    {
        PendingSyncCount = value.PendingSyncCount;
    }

    public void PrepareForEntry(PosSessionState session)
    {
        Session = session;
        _pendingVoucherUploadOrderGuid = null;
        _pendingVoucherTenderedAmount = 0m;
        _pendingVoucherChangeAmount = 0m;
        PaymentTenders.Clear();
        VoucherCodeText = string.Empty;
        TenderAmountText = string.Empty;
        _statusKey = "payment.status.ready";
        _statusTextOverride = null;
        SelectedPaymentMethod = PaymentMethodKind.Cash;
        RefreshCart();
        OnPropertyChanged(nameof(StatusMessage));
    }

    public void RefreshCart()
    {
        CartLines.ReplaceWith(_cart.Lines);
        OnPropertyChanged(nameof(TotalAmount));
        OnPropertyChanged(nameof(DiscountAmount));
        OnPropertyChanged(nameof(ActualAmount));
        RecalculateTenderSummary();
        RefreshCartValidationStatus();
        SyncTenderAmountToRemaining();
        AddTenderCommand.NotifyCanExecuteChanged();
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
    }

    private void AppendTenderAmount(string? value)
    {
        if (value == "Back")
        {
            TenderAmountText = TenderAmountText.Length > 0 ? TenderAmountText[..^1] : string.Empty;
            return;
        }

        if (value == "Clear")
        {
            TenderAmountText = string.Empty;
            return;
        }

        if (value == "." && TenderAmountText.Contains('.', StringComparison.Ordinal))
        {
            return;
        }

        TenderAmountText += value;
    }

    private void ApplyQuickCash(decimal amount)
    {
        TenderAmountText = amount.ToString("0.00");
    }

    private async Task AddTenderAsync()
    {
        if (_pendingVoucherUploadOrderGuid is not null)
        {
            SetStatus("payment.status.retryVoucherUpload");
            return;
        }

        if (TrySetBlockingCartIssueStatus())
        {
            AddTenderCommand.NotifyCanExecuteChanged();
            ConfirmPaymentCommand.NotifyCanExecuteChanged();
            return;
        }

        var result = await _workflowService.AddTenderAsync(
            SelectedPaymentMethod,
            Session,
            ActualAmount,
            PaymentTenders.ToList(),
            TenderAmountText,
            IsVoucherSelected ? VoucherCodeText : null);

        if (!result.Succeeded || result.Tender is null)
        {
            SetStatus(result.StatusKey, result.StatusMessage);
            return;
        }

        PaymentTenders.Add(result.Tender);
        if (IsVoucherSelected)
        {
            VoucherCodeText = string.Empty;
        }

        RecalculateTenderSummary();
        SyncTenderAmountToRemaining(force: true);
        SetStatus(result.StatusKey);
        AddTenderCommand.NotifyCanExecuteChanged();
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
    }

    private void RemoveTender(PaymentTender? tender)
    {
        if (tender is null)
        {
            return;
        }

        if (_pendingVoucherUploadOrderGuid is not null)
        {
            SetStatus("payment.status.retryVoucherUpload");
            return;
        }

        PaymentTenders.Remove(tender);
        RecalculateTenderSummary();
        SyncTenderAmountToRemaining(force: true);
        SetStatus("payment.status.tenderRemoved");
        AddTenderCommand.NotifyCanExecuteChanged();
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
    }

    private async Task ConfirmPaymentAsync()
    {
        if (TrySetBlockingCartIssueStatus())
        {
            ConfirmPaymentCommand.NotifyCanExecuteChanged();
            return;
        }

        if (_pendingVoucherUploadOrderGuid is not null)
        {
            await RetryPendingVoucherUploadAsync();
            return;
        }

        if (PaymentTenders.Count == 0 && CanAddImplicitCashTender())
        {
            await AddTenderAsync();
        }

        if (PaymentTenders.Count == 0)
        {
            SetStatus("payment.status.noTendersAdded");
            return;
        }

        if (TotalTendered < ActualAmount)
        {
            SetStatus("payment.status.remainingBalance");
            return;
        }

        var cashTenderedAmount = PaymentTenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount);
        CashPaymentWorkflowResult result;
        try
        {
            result = await _workflowService.CompletePaymentAsync(
                _cart,
                Session,
                PaymentTenders.ToList(),
                cashTenderedAmount);
        }
        catch (PaymentUploadFailedException ex)
        {
            _pendingVoucherUploadOrderGuid = ex.OrderGuid;
            _pendingVoucherTenderedAmount = ex.TenderedAmount;
            _pendingVoucherChangeAmount = ex.ChangeAmount;
            SetStatus("payment.status.uploadFailed", ex.Message);
            ConfirmPaymentCommand.NotifyCanExecuteChanged();
            AddTenderCommand.NotifyCanExecuteChanged();
            return;
        }

        CompleteSuccessfulPayment(result);
    }

    private async Task RetryPendingVoucherUploadAsync()
    {
        if (_pendingVoucherUploadOrderGuid is null)
        {
            return;
        }

        try
        {
            var result = await _workflowService.RetryVoucherUploadAsync(
                _pendingVoucherUploadOrderGuid.Value,
                _cart,
                Session,
                _pendingVoucherTenderedAmount,
                _pendingVoucherChangeAmount);
            CompleteSuccessfulPayment(result);
        }
        catch (PaymentUploadFailedException ex)
        {
            _pendingVoucherUploadOrderGuid = ex.OrderGuid;
            _pendingVoucherTenderedAmount = ex.TenderedAmount;
            _pendingVoucherChangeAmount = ex.ChangeAmount;
            SetStatus("payment.status.uploadFailed", ex.Message);
        }
    }

    private void CompleteSuccessfulPayment(CashPaymentWorkflowResult result)
    {
        _pendingVoucherUploadOrderGuid = null;
        _pendingVoucherTenderedAmount = 0m;
        _pendingVoucherChangeAmount = 0m;
        PaymentTenders.Clear();
        PendingSyncCount = result.PendingSyncCount;
        Session = result.UpdatedSession;
        RefreshCart();
        SetStatus("payment.status.completed");
        PaymentCompleted?.Invoke(this, new PaymentCompletedEventArgs(result.Order, result.TenderedAmount, result.ChangeAmount));
    }

    private bool CanAddTender()
    {
        if (_pendingVoucherUploadOrderGuid is not null ||
            _cart.IsEmpty || _cart.HasNonIntegerQuantity || _cart.HasReturnLine || _cart.HasZeroPriceLine)
        {
            return false;
        }

        if (!_workflowService.TryParseTenderedAmount(TenderAmountText, out var amount) || amount <= 0m)
        {
            return false;
        }

        var remainingAmount = Math.Max(0m, RemainingAmount);
        if (remainingAmount <= 0m)
        {
            return false;
        }

        if (SelectedPaymentMethod == PaymentMethodKind.Voucher && string.IsNullOrWhiteSpace(VoucherCodeText))
        {
            return false;
        }

        return SelectedPaymentMethod == PaymentMethodKind.Cash || amount <= remainingAmount;
    }

    private bool CanConfirmPayment()
    {
        if (_pendingVoucherUploadOrderGuid is not null)
        {
            return true;
        }

        return !_cart.IsEmpty &&
            !_cart.HasNonIntegerQuantity &&
            !_cart.HasReturnLine &&
            !_cart.HasZeroPriceLine &&
            ((PaymentTenders.Count > 0 && TotalTendered >= ActualAmount) || CanAddImplicitCashTender());
    }

    private bool CanAddImplicitCashTender()
    {
        return SelectedPaymentMethod == PaymentMethodKind.Cash &&
            PaymentTenders.Count == 0 &&
            TryParseEnoughCashTender();
    }

    private bool TryParseEnoughCashTender()
    {
        return _workflowService.TryParseTenderedAmount(TenderAmountText, out var amount) &&
            amount >= ActualAmount &&
            ActualAmount > 0m;
    }

    private void RefreshCartValidationStatus()
    {
        if (TrySetBlockingCartIssueStatus())
        {
            return;
        }

        if (_statusTextOverride is null &&
            _statusKey is "cart.status.quantityMustBeInteger" or "cart.status.zeroPriceItem" or "payment.cash.status.returnCheckoutNotReady")
        {
            SetStatus("payment.status.ready");
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

    private void RecalculateTenderSummary()
    {
        TotalTendered = _workflowService.CalculateTenderedAmount(PaymentTenders.ToList());
        RemainingAmount = Math.Max(0m, _workflowService.CalculateRemainingAmount(ActualAmount, PaymentTenders.ToList()));
        ChangeDue = PaymentTenders.Count == 0 && _workflowService.TryParseTenderedAmount(TenderAmountText, out var tenderedAmount)
            ? Math.Max(0m, decimal.Round(tenderedAmount - ActualAmount, 2, MidpointRounding.AwayFromZero))
            : _workflowService.CalculateChange(PaymentTenders.ToList(), ActualAmount);
        OnPropertyChanged(nameof(QuickCashAmounts));
    }

    private IReadOnlyList<decimal> BuildQuickCashAmounts()
    {
        var amountDue = RemainingAmount > 0m ? RemainingAmount : ActualAmount;
        if (amountDue <= 0m)
        {
            return [5m, 10m, 20m, 50m];
        }

        var rounded = Math.Ceiling(amountDue);
        var amounts = new SortedSet<decimal>
        {
            amountDue,
            rounded,
            NextMultiple(amountDue, 5m),
            NextMultiple(amountDue, 10m),
            NextMultiple(amountDue, 20m),
            NextMultiple(amountDue, 50m)
        };

        return amounts.Where(amount => amount >= amountDue).Take(6).ToList();
    }

    private static decimal NextMultiple(decimal value, decimal multiple)
    {
        return Math.Ceiling(value / multiple) * multiple;
    }

    private void SyncTenderAmountToRemaining(bool force = false)
    {
        if (!force && !string.IsNullOrWhiteSpace(TenderAmountText))
        {
            return;
        }

        var nextAmount = RemainingAmount > 0m
            ? RemainingAmount
            : ActualAmount > 0m ? ActualAmount : 0m;
        TenderAmountText = nextAmount > 0m ? nextAmount.ToString("0.00") : string.Empty;
    }

    private void SetStatus(string key, string? statusText = null)
    {
        _statusKey = key;
        _statusTextOverride = statusText;
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
        OnPropertyChanged(nameof(CurrentTenderTextLabel));
        OnPropertyChanged(nameof(RemainingAmountText));
        OnPropertyChanged(nameof(ChangeDueText));
        OnPropertyChanged(nameof(QuickCashText));
        OnPropertyChanged(nameof(PaymentMethodText));
        OnPropertyChanged(nameof(AppliedTendersText));
        OnPropertyChanged(nameof(AddTenderText));
        OnPropertyChanged(nameof(ConfirmPaymentText));
        OnPropertyChanged(nameof(NoTendersText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(StatusMessage));
    }
}
