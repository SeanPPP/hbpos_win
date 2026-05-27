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
    private CancellationTokenSource? _activeCardPaymentCts;
    private CancellationTokenSource? _manuallyCancelledCardPaymentCts;
    private bool _cardPaymentCancellationRequested;
    private bool _awaitingLateCardResultAfterManualCancel;
    private bool _discardLateCardResultAfterManualCancel;
    private int _paymentEntryVersion;

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

    [ObservableProperty]
    private bool _isCardPaymentInProgress;

    [ObservableProperty]
    private bool _isPaymentInteractionLocked;

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

        NumberInputCommand = new RelayCommand<string>(AppendTenderAmount, _ => IsPaymentInteractionEnabled);
        QuickCashCommand = new AsyncRelayCommand<QuickCashOption>(ApplyQuickCashAsync, CanApplyQuickCash);
        SelectCashCommand = new AsyncRelayCommand(() => AddTenderByMethodAsync(PaymentMethodKind.Cash), () => CanAddTender(PaymentMethodKind.Cash, allowDefaultAmount: true));
        SelectCardCommand = new AsyncRelayCommand(
            () => AddTenderByMethodAsync(PaymentMethodKind.Card),
            () => CanAddTender(PaymentMethodKind.Card, allowDefaultAmount: true),
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        SelectVoucherCommand = new AsyncRelayCommand(() => AddTenderByMethodAsync(PaymentMethodKind.Voucher), () => CanAddTender(PaymentMethodKind.Voucher, allowDefaultAmount: true));
        AddTenderCommand = new AsyncRelayCommand(AddTenderAsync, CanAddTender);
        RemoveTenderCommand = new RelayCommand<PaymentTender>(RemoveTender, CanRemoveTender);
        ConfirmPaymentCommand = new AsyncRelayCommand(ConfirmPaymentAsync, CanConfirmPayment);
        CancelCommand = new RelayCommand(CancelPayment, CanCancelPayment);

        RefreshCart();
    }

    public ObservableCollection<CartLine> CartLines { get; } = [];

    public ObservableCollection<PaymentTender> PaymentTenders { get; } = [];

    public IReadOnlyList<QuickCashOption> QuickCashAmounts => BuildQuickCashAmounts();

    public IRelayCommand<string> NumberInputCommand { get; }

    public IAsyncRelayCommand<QuickCashOption> QuickCashCommand { get; }

    public IAsyncRelayCommand SelectCashCommand { get; }

    public IAsyncRelayCommand SelectCardCommand { get; }

    public IAsyncRelayCommand SelectVoucherCommand { get; }

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

    public bool IsPaymentInteractionEnabled => !IsPaymentInteractionLocked;

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

    public bool IsConfirmPaymentVisible => CanConfirmPayment();

    partial void OnTenderAmountTextChanged(string value)
    {
        RecalculateTenderSummary();
        NotifyPaymentCommandStates();
    }

    partial void OnVoucherCodeTextChanged(string value)
    {
        NotifyPaymentCommandStates();
    }

    partial void OnSelectedPaymentMethodChanged(PaymentMethodKind value)
    {
        AddTenderCommand.NotifyCanExecuteChanged();
        SelectCashCommand.NotifyCanExecuteChanged();
        SelectCardCommand.NotifyCanExecuteChanged();
        SelectVoucherCommand.NotifyCanExecuteChanged();
        QuickCashCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsCashSelected));
        OnPropertyChanged(nameof(IsCardSelected));
        OnPropertyChanged(nameof(IsVoucherSelected));
        OnPropertyChanged(nameof(QuickCashAmounts));
    }

    partial void OnIsCardPaymentInProgressChanged(bool value)
    {
        NotifyPaymentCommandStates();
    }

    partial void OnIsPaymentInteractionLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPaymentInteractionEnabled));
        NotifyPaymentCommandStates();
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
        _paymentEntryVersion++;
        _awaitingLateCardResultAfterManualCancel = false;
        _discardLateCardResultAfterManualCancel = false;
        CancelActiveCardPayment();
        DetachCanceledActiveCardPayment();
        IsCardPaymentInProgress = false;
        IsPaymentInteractionLocked = false;
        PaymentTenders.Clear();
        VoucherCodeText = string.Empty;
        TenderAmountText = string.Empty;
        _statusKey = "payment.status.ready";
        _statusTextOverride = null;
        SelectedPaymentMethod = PaymentMethodKind.Cash;
        RefreshCart();
        SyncTenderAmountToRemaining(force: true);
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
        NotifyPaymentCommandStates();
    }

    private void AppendTenderAmount(string? value)
    {
        if (IsPaymentInteractionLocked || _activeCardPaymentCts is not null)
        {
            return;
        }

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

    private async Task ApplyQuickCashAsync(QuickCashOption? option)
    {
        if (option is null)
        {
            return;
        }

        TenderAmountText = option.Amount.ToString("0.00");
        await AddTenderByMethodAsync(PaymentMethodKind.Cash);
    }

    private async Task AddTenderAsync()
    {
        await AddTenderByMethodAsync(SelectedPaymentMethod);
    }

    private async Task AddTenderByMethodAsync(PaymentMethodKind method)
    {
        if (IsPaymentInteractionLocked)
        {
            return;
        }

        if (_pendingVoucherUploadOrderGuid is not null)
        {
            SetStatus("payment.status.retryVoucherUpload");
            return;
        }

        if (HasTenderForMethod(method))
        {
            SelectedPaymentMethod = method;
            SetStatus("payment.status.duplicatePaymentMethod");
            NotifyPaymentCommandStates();
            return;
        }

        if (TrySetBlockingCartIssueStatus())
        {
            NotifyPaymentCommandStates();
            return;
        }

        SelectedPaymentMethod = method;
        if (method == PaymentMethodKind.Voucher && string.IsNullOrWhiteSpace(VoucherCodeText))
        {
            SetStatus("payment.status.voucherCodeRequired");
            NotifyPaymentCommandStates();
            return;
        }

        var amountText = ResolveTenderAmountText(method);
        PaymentTenderAttemptResult result;
        CancellationTokenSource? cardPaymentCts = null;
        var cardPaymentWasManuallyCancelled = false;
        var paymentEntryVersion = _paymentEntryVersion;
        try
        {
            if (method == PaymentMethodKind.Card)
            {
                _cardPaymentCancellationRequested = false;
                _awaitingLateCardResultAfterManualCancel = false;
                _discardLateCardResultAfterManualCancel = false;
                _activeCardPaymentCts?.Dispose();
                _activeCardPaymentCts = new CancellationTokenSource();
                cardPaymentCts = _activeCardPaymentCts;
                IsCardPaymentInProgress = true;
                IsPaymentInteractionLocked = true;
                SetStatus("payment.status.cardProcessing");
            }

            result = await _workflowService.AddTenderAsync(
                method,
                Session,
                ActualAmount,
                PaymentTenders.ToList(),
                amountText,
                method == PaymentMethodKind.Voucher ? VoucherCodeText : null,
                method == PaymentMethodKind.Card ? cardPaymentCts?.Token ?? CancellationToken.None : CancellationToken.None);
            if (method == PaymentMethodKind.Card && cardPaymentCts?.IsCancellationRequested == true)
            {
                cardPaymentWasManuallyCancelled = IsManualCardCancellation(cardPaymentCts);
            }
        }
        catch (OperationCanceledException) when (method == PaymentMethodKind.Card)
        {
            if (IsCurrentPaymentEntry(paymentEntryVersion))
            {
                SetCardCancellationStatus(IsManualCardCancellation(cardPaymentCts));
                ResetManualCardCancellationState();
                NotifyPaymentCommandStates();
            }

            return;
        }
        finally
        {
            if (method == PaymentMethodKind.Card)
            {
                ClearActiveCardPayment(cardPaymentCts);
            }
        }

        if (!IsCurrentPaymentEntry(paymentEntryVersion))
        {
            return;
        }

        if (method == PaymentMethodKind.Card &&
            cardPaymentCts?.IsCancellationRequested == true &&
            (!result.Succeeded || result.Tender is null))
        {
            if (cardPaymentWasManuallyCancelled && IsConfirmedCardCancellation(result.StatusMessage))
            {
                SetCardCancellationStatus(wasManuallyCancelled: true);
            }
            else if (!cardPaymentWasManuallyCancelled)
            {
                SetCardCancellationStatus(wasManuallyCancelled: false);
            }
            else
            {
                SetStatus(result.StatusKey, result.StatusMessage);
            }

            ResetManualCardCancellationState();
            NotifyPaymentCommandStates();
            return;
        }

        if (method == PaymentMethodKind.Card && _discardLateCardResultAfterManualCancel)
        {
            SetCardCancellationStatus(wasManuallyCancelled: true);
            ResetManualCardCancellationState();
            NotifyPaymentCommandStates();
            return;
        }

        if (!result.Succeeded || result.Tender is null)
        {
            if (method == PaymentMethodKind.Card && TrySetCardTerminalFailureStatus(result))
            {
                ResetManualCardCancellationState();
                NotifyPaymentCommandStates();
                return;
            }

            SetStatus(result.StatusKey, result.StatusMessage);
            ResetManualCardCancellationState();
            NotifyPaymentCommandStates();
            return;
        }

        ResetManualCardCancellationState();
        PaymentTenders.Add(result.Tender);
        if (method == PaymentMethodKind.Voucher)
        {
            VoucherCodeText = string.Empty;
        }

        TenderAmountText = string.Empty;
        RecalculateTenderSummary();
        SetStatus(result.StatusKey);
        NotifyPaymentCommandStates();
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
        TenderAmountText = string.Empty;
        SetStatus("payment.status.tenderRemoved");
        NotifyPaymentCommandStates();
    }

    private bool CanRemoveTender(PaymentTender? tender)
    {
        return tender is not null &&
            IsPaymentInteractionEnabled &&
            _activeCardPaymentCts is null &&
            _pendingVoucherUploadOrderGuid is null;
    }

    private async Task ConfirmPaymentAsync()
    {
        if (IsPaymentInteractionLocked)
        {
            return;
        }

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

        if (PaymentTenders.Count == 0)
        {
            SetStatus("payment.status.noTendersAdded");
            return;
        }

        if (RemainingAmount > 0m)
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
            NotifyPaymentCommandStates();
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
        return CanAddTender(SelectedPaymentMethod, allowDefaultAmount: false);
    }

    private bool CanAddTender(PaymentMethodKind method, bool allowDefaultAmount)
    {
        if (IsPaymentInteractionLocked ||
            _activeCardPaymentCts is not null ||
            _pendingVoucherUploadOrderGuid is not null ||
            _cart.IsEmpty || _cart.HasNonIntegerQuantity || _cart.HasReturnLine || _cart.HasZeroPriceLine)
        {
            return false;
        }

        if (HasTenderForMethod(method))
        {
            return false;
        }

        var amountText = ResolveTenderAmountText(method, allowDefaultAmount);
        if (!_workflowService.TryParseTenderedAmount(amountText, out var amount) || amount <= 0m)
        {
            return false;
        }

        var remainingAmount = method == PaymentMethodKind.Cash
            ? Math.Max(0m, RemainingAmount)
            : GetExternalRemainingAmount();
        if (remainingAmount <= 0m)
        {
            return false;
        }

        if (method == PaymentMethodKind.Voucher && !allowDefaultAmount && string.IsNullOrWhiteSpace(VoucherCodeText))
        {
            return false;
        }

        return method == PaymentMethodKind.Cash || amount <= remainingAmount;
    }

    private bool CanConfirmPayment()
    {
        if (IsPaymentInteractionLocked || _activeCardPaymentCts is not null)
        {
            return false;
        }

        if (_pendingVoucherUploadOrderGuid is not null)
        {
            return true;
        }

        return !_cart.IsEmpty &&
            !_cart.HasNonIntegerQuantity &&
            !_cart.HasReturnLine &&
            !_cart.HasZeroPriceLine &&
            PaymentTenders.Count > 0 &&
            RemainingAmount <= 0m;
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
            ? Math.Max(0m, CashRoundingPolicy.CalculateCashChange(ActualAmount, [], tenderedAmount))
            : _workflowService.CalculateChange(PaymentTenders.ToList(), ActualAmount);
        OnPropertyChanged(nameof(QuickCashAmounts));
    }

    private IReadOnlyList<QuickCashOption> BuildQuickCashAmounts()
    {
        return
        [
            new QuickCashOption(5m, "$5", "#FFC15AA1", "White"),
            new QuickCashOption(10m, "$10", "#FF2E6BB8", "White"),
            new QuickCashOption(20m, "$20", "#FFE45858", "White"),
            new QuickCashOption(50m, "$50", "#FFF2C94C", "#FF2E1500"),
            new QuickCashOption(100m, "$100", "#FF2F9E6D", "White")
        ];
    }

    private void SyncTenderAmountToRemaining(bool force = false)
    {
        if (!force && !string.IsNullOrWhiteSpace(TenderAmountText))
        {
            return;
        }

        var amount = SelectedPaymentMethod == PaymentMethodKind.Cash
            ? CashRoundingPolicy.GetCashPayableAmount(ActualAmount, PaymentTenders.ToList())
            : GetExternalRemainingAmount();
        TenderAmountText = amount > 0m ? amount.ToString("0.00") : string.Empty;
    }

    private bool CanApplyQuickCash(QuickCashOption? option)
    {
        return option is not null && CanAddTender(PaymentMethodKind.Cash, allowDefaultAmount: true);
    }

    private string ResolveTenderAmountText(PaymentMethodKind method)
    {
        return ResolveTenderAmountText(method, allowDefaultAmount: true);
    }

    private string ResolveTenderAmountText(PaymentMethodKind method, bool allowDefaultAmount)
    {
        if (!string.IsNullOrWhiteSpace(TenderAmountText) || !allowDefaultAmount)
        {
            return TenderAmountText;
        }

        var amount = method == PaymentMethodKind.Cash
            ? CashRoundingPolicy.GetCashPayableAmount(ActualAmount, PaymentTenders.ToList())
            : GetExternalRemainingAmount();
        return amount > 0m ? amount.ToString("0.00") : string.Empty;
    }

    private decimal GetExternalRemainingAmount()
    {
        var tenderedAmount = PaymentTenders.Sum(tender => tender.Amount);
        return Math.Max(0m, decimal.Round(ActualAmount - tenderedAmount, 2, MidpointRounding.AwayFromZero));
    }

    private bool HasTenderForMethod(PaymentMethodKind method)
    {
        return PaymentTenders.Any(tender => tender.Method == method);
    }

    private void CancelPayment()
    {
        if (IsCardPaymentInProgress)
        {
            CancelActiveCardPayment();
            return;
        }

        if (_awaitingLateCardResultAfterManualCancel)
        {
            _discardLateCardResultAfterManualCancel = true;
            NotifyPaymentCommandStates();
            PaymentCancelled?.Invoke(this, EventArgs.Empty);
            return;
        }

        PaymentCancelled?.Invoke(this, EventArgs.Empty);
    }

    private bool CanCancelPayment()
    {
        return IsCardPaymentInProgress ||
            _awaitingLateCardResultAfterManualCancel ||
            (IsPaymentInteractionEnabled && _activeCardPaymentCts is null);
    }

    private void CancelActiveCardPayment()
    {
        if (_activeCardPaymentCts is null || _activeCardPaymentCts.IsCancellationRequested)
        {
            return;
        }

        _cardPaymentCancellationRequested = true;
        _awaitingLateCardResultAfterManualCancel = true;
        _discardLateCardResultAfterManualCancel = false;
        _manuallyCancelledCardPaymentCts = _activeCardPaymentCts;
        _activeCardPaymentCts.Cancel();
        IsCardPaymentInProgress = false;
        IsPaymentInteractionLocked = false;
        SetStatus("payment.status.cardCancelled");
        NotifyPaymentCommandStates();
    }

    private void ClearActiveCardPayment(CancellationTokenSource? cardPaymentCts)
    {
        if (!ReferenceEquals(_activeCardPaymentCts, cardPaymentCts))
        {
            return;
        }

        IsCardPaymentInProgress = false;
        IsPaymentInteractionLocked = false;
        _activeCardPaymentCts?.Dispose();
        _activeCardPaymentCts = null;
        if (ReferenceEquals(_manuallyCancelledCardPaymentCts, cardPaymentCts))
        {
            _manuallyCancelledCardPaymentCts = null;
        }

        _cardPaymentCancellationRequested = false;
    }

    private void DetachCanceledActiveCardPayment()
    {
        if (_activeCardPaymentCts?.IsCancellationRequested != true)
        {
            return;
        }

        if (ReferenceEquals(_manuallyCancelledCardPaymentCts, _activeCardPaymentCts))
        {
            _manuallyCancelledCardPaymentCts = null;
        }

        _activeCardPaymentCts = null;
        _cardPaymentCancellationRequested = false;
    }

    private void SetCardCancellationStatus(bool wasManuallyCancelled)
    {
        SetStatus(wasManuallyCancelled ? "payment.status.cardCancelled" : "payment.status.cardTimedOut");
    }

    private bool IsManualCardCancellation(CancellationTokenSource? cardPaymentCts)
    {
        return _cardPaymentCancellationRequested || ReferenceEquals(_manuallyCancelledCardPaymentCts, cardPaymentCts);
    }

    private bool TrySetCardTerminalFailureStatus(PaymentTenderAttemptResult result)
    {
        if (IsConfirmedCardCancellation(result.StatusMessage))
        {
            SetStatus("payment.status.cardCancelled");
            return true;
        }

        if (IsTimeoutMessage(result.StatusMessage))
        {
            SetStatus("payment.status.cardTimedOut");
            return true;
        }

        return false;
    }

    private static bool IsTimeoutMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message) &&
            (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("timeout", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsConfirmedCardCancellation(string? message)
    {
        return !string.IsNullOrWhiteSpace(message) &&
            message.Contains("cancel", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("could not be confirmed", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCurrentPaymentEntry(int paymentEntryVersion)
    {
        return paymentEntryVersion == _paymentEntryVersion;
    }

    private void ResetManualCardCancellationState()
    {
        _awaitingLateCardResultAfterManualCancel = false;
        _discardLateCardResultAfterManualCancel = false;
    }

    private void NotifyPaymentCommandStates()
    {
        NumberInputCommand.NotifyCanExecuteChanged();
        AddTenderCommand.NotifyCanExecuteChanged();
        SelectCashCommand.NotifyCanExecuteChanged();
        SelectCardCommand.NotifyCanExecuteChanged();
        SelectVoucherCommand.NotifyCanExecuteChanged();
        QuickCashCommand.NotifyCanExecuteChanged();
        RemoveTenderCommand.NotifyCanExecuteChanged();
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsConfirmPaymentVisible));
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

public sealed record QuickCashOption(decimal Amount, string Label, string NoteColorKey, string ForegroundColorKey);
