using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

public partial class PaymentViewModel : ObservableObject
{
    private readonly PosCartService _cart;
    private readonly ICashPaymentWorkflowService _workflowService;
    private readonly ILocalizationService? _localization;
    private readonly Action? _onBackToPos;
    private readonly Action? _onShowInstallmentCenter;
    private readonly Func<Task>? _recoverPreviousCardTransactionAsync;
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
    private decimal _workflowRemainingAmount;

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

    [ObservableProperty]
    private PaymentEntryMode _paymentMode;

    [ObservableProperty]
    private CardPaymentErrorOverlayViewModel? _cardPaymentErrorOverlay;

    public PaymentViewModel(
        PosCartService cart,
        CashCheckoutService checkout,
        ILocalOrderRepository orderRepository,
        ISyncQueueRepository syncQueueRepository,
        PosSessionState session,
        ILocalizationService? localization = null,
        Action? onBackToPos = null,
        Action? onShowInstallmentCenter = null,
        Func<Task>? recoverPreviousCardTransactionAsync = null)
        : this(
            cart,
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueueRepository),
            session,
            localization,
            onBackToPos,
            onShowInstallmentCenter,
            recoverPreviousCardTransactionAsync)
    {
    }

    public PaymentViewModel(
        PosCartService cart,
        ICashPaymentWorkflowService workflowService,
        PosSessionState session,
        ILocalizationService? localization = null,
        Action? onBackToPos = null,
        Action? onShowInstallmentCenter = null,
        Func<Task>? recoverPreviousCardTransactionAsync = null)
    {
        _cart = cart;
        _workflowService = workflowService;
        _session = session;
        _localization = localization;
        _onBackToPos = onBackToPos;
        _onShowInstallmentCenter = onShowInstallmentCenter;
        _recoverPreviousCardTransactionAsync = recoverPreviousCardTransactionAsync;
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
        RemoveTenderCommand = new RelayCommand<PaymentTender>(RemoveTender, CanRemoveTender);
        ConfirmPaymentCommand = new AsyncRelayCommand(ConfirmPaymentAsync, CanConfirmPayment);
        CancelCommand = new RelayCommand(CancelPayment, CanCancelPayment);
        BackToPosCommand = new RelayCommand(BackToPos, CanBackToPos);
        ShowInstallmentCenterCommand = new RelayCommand(ShowInstallmentCenter, CanShowInstallmentCenter);
        CloseCardPaymentErrorOverlayCommand = new RelayCommand(CloseCardPaymentErrorOverlay);
        CardPaymentErrorPrimaryActionCommand = new AsyncRelayCommand(
            ExecuteCardPaymentErrorPrimaryActionAsync,
            CanExecuteCardPaymentErrorPrimaryAction);

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

    public IRelayCommand<PaymentTender> RemoveTenderCommand { get; }

    public IAsyncRelayCommand ConfirmPaymentCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand BackToPosCommand { get; }

    public IRelayCommand ShowInstallmentCenterCommand { get; }

    public IRelayCommand CloseCardPaymentErrorOverlayCommand { get; }

    public IAsyncRelayCommand CardPaymentErrorPrimaryActionCommand { get; }

    public event EventHandler<PaymentCompletedEventArgs>? PaymentCompleted;

    public string ScreenTitleText => T(GetScreenTitleKey());

    public string OrderSummaryText => T("payment.orderSummary");

    public string CurrentTenderTextLabel => T("payment.currentTender");

    public string AmountTenderedTextLabel => CurrentTenderTextLabel;

    public string RemainingAmountText => T(GetRemainingAmountKey());

    public string ChangeDueText => T("payment.changeDue");

    public string QuickCashText => T("payment.quickCash");

    public string PaymentMethodText => T("payment.method");

    public string AppliedTendersText => T("payment.appliedTenders");

    public string ConfirmPaymentText => T(GetConfirmPaymentKey());

    public string NoTendersText => T(GetNoTendersKey());

    public string CashMethodText => T(IsRefundMode ? "payment.method.refundCash" : "payment.method.cash");

    public string CardMethodText => T(IsRefundMode ? "payment.method.refundCard" : "payment.method.card");

    public string VoucherMethodText => T(IsRefundMode ? "payment.method.refundVoucher" : "payment.method.voucher");

    public string InstallmentMethodText => T("payment.method.installment");

    public string CancelText => T("common.cancel");

    public string StatusMessage => _statusTextOverride ?? T(_statusKey);

    public bool IsPaymentInteractionEnabled => !IsPaymentInteractionLocked;

    // 普通支付状态隐藏取消入口，避免将取消误用为返回收银页。
    public bool IsCancelPaymentVisible => IsCardPaymentInProgress || _awaitingLateCardResultAfterManualCancel;

    public decimal TotalAmount => _cart.TotalAmount;

    public decimal DiscountAmount => _cart.DiscountAmount;

    public decimal ActualAmount => _cart.ActualAmount;

    public bool IsRefundMode => PaymentMode == PaymentEntryMode.Refund;

    public bool IsZeroSettlementMode => PaymentMode == PaymentEntryMode.ZeroSettlement;

    public bool IsPaymentMode => PaymentMode == PaymentEntryMode.Payment;

    public bool IsTenderEntryVisible => !IsZeroSettlementMode;

    public bool IsPaymentMethodSelectionVisible => !IsZeroSettlementMode;

    public bool IsQuickCashVisible => IsPaymentMode && IsCashSelected;

    public string AmountTenderedText
    {
        get => TenderAmountText;
        set => TenderAmountText = value;
    }

    public bool IsCashSelected => SelectedPaymentMethod == PaymentMethodKind.Cash;

    public bool IsCardSelected => SelectedPaymentMethod == PaymentMethodKind.Card;

    public bool IsVoucherSelected => SelectedPaymentMethod == PaymentMethodKind.Voucher;

    public bool IsVoucherCodeEntryVisible => IsVoucherSelected && !IsRefundMode;

    public bool IsInstallmentEntryVisible => IsPaymentMode;

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
        SelectCashCommand.NotifyCanExecuteChanged();
        SelectCardCommand.NotifyCanExecuteChanged();
        SelectVoucherCommand.NotifyCanExecuteChanged();
        QuickCashCommand.NotifyCanExecuteChanged();
        ShowInstallmentCenterCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsCashSelected));
        OnPropertyChanged(nameof(IsCardSelected));
        OnPropertyChanged(nameof(IsVoucherSelected));
        OnPropertyChanged(nameof(IsVoucherCodeEntryVisible));
        OnPropertyChanged(nameof(QuickCashAmounts));
        OnPropertyChanged(nameof(IsQuickCashVisible));
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
        NotifyPaymentCommandStates();
    }

    partial void OnPaymentModeChanged(PaymentEntryMode value)
    {
        OnPropertyChanged(nameof(ScreenTitleText));
        OnPropertyChanged(nameof(RemainingAmountText));
        OnPropertyChanged(nameof(ConfirmPaymentText));
        OnPropertyChanged(nameof(NoTendersText));
        OnPropertyChanged(nameof(CashMethodText));
        OnPropertyChanged(nameof(CardMethodText));
        OnPropertyChanged(nameof(VoucherMethodText));
        OnPropertyChanged(nameof(InstallmentMethodText));
        OnPropertyChanged(nameof(IsRefundMode));
        OnPropertyChanged(nameof(IsZeroSettlementMode));
        OnPropertyChanged(nameof(IsPaymentMode));
        OnPropertyChanged(nameof(IsTenderEntryVisible));
        OnPropertyChanged(nameof(IsPaymentMethodSelectionVisible));
        OnPropertyChanged(nameof(IsQuickCashVisible));
        OnPropertyChanged(nameof(IsVoucherCodeEntryVisible));
        OnPropertyChanged(nameof(IsInstallmentEntryVisible));
        ShowInstallmentCenterCommand.NotifyCanExecuteChanged();
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
        _statusKey = GetReadyStatusKey();
        _statusTextOverride = null;
        SelectedPaymentMethod = PaymentMethodKind.Cash;
        RefreshCart();
        if (!HasBlockingCartIssue())
        {
            SetStatus(GetReadyStatusKey());
        }

        OnPropertyChanged(nameof(StatusMessage));
    }

    public void RefreshCart()
    {
        PaymentMode = CalculatePaymentMode();
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

        if (IsOfflineVoucherRefundUnavailable(method))
        {
            SetOfflineVoucherRefundUnavailableStatus();
            return;
        }

        if (HasTenderForMethod(method) && !(IsRefundMode && method == PaymentMethodKind.Card))
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

        var shouldUseMethodDefaultAmount = IsRefundMode &&
            method == PaymentMethodKind.Card &&
            SelectedPaymentMethod != method;
        SelectedPaymentMethod = method;
        if (!IsRefundMode &&
            method == PaymentMethodKind.Voucher &&
            string.IsNullOrWhiteSpace(VoucherCodeText))
        {
            SetStatus("payment.status.voucherCodeRequired");
            NotifyPaymentCommandStates();
            return;
        }

        var amountText = shouldUseMethodDefaultAmount
            ? ResolveDefaultTenderAmountText(method)
            : ResolveTenderAmountText(method);
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

            var referenceText = method == PaymentMethodKind.Voucher
                ? VoucherCodeText
                : IsRefundMode && method == PaymentMethodKind.Card
                    ? GetRefundReference(method)
                    : null;
            if (IsRefundMode && method == PaymentMethodKind.Card)
            {
                ConsoleLog.Write(
                    "CardRefund",
                    $"payment view add card refund amountText={LogValue(amountText)} originalReference={LogValue(referenceText)} " +
                    $"tenders={PaymentTenders.Count} capacities={_cart.ReturnPaymentCapacities.Count}");
            }

            result = await _workflowService.AddTenderAsync(
                method,
                Session,
                ActualAmount,
                PaymentTenders.ToList(),
                amountText,
                referenceText,
                method == PaymentMethodKind.Card ? cardPaymentCts?.Token ?? CancellationToken.None : CancellationToken.None,
                method == PaymentMethodKind.Card ? _cart.CreateSnapshot() : null);
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
        catch (Exception ex) when (method == PaymentMethodKind.Card && ex is not OperationCanceledException)
        {
            ConsoleLog.Write("CardPayment", $"unexpected card payment exception: {ex}");
            if (IsCurrentPaymentEntry(paymentEntryVersion))
            {
                var overlay = CardPaymentErrorOverlayViewModel.Unexpected();
                overlay.IsOpen = true;
                CardPaymentErrorOverlay = overlay;
                SetStatus("payment.card.status.failed", ex.Message);
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
            if (IsRefundMode && method == PaymentMethodKind.Card)
            {
                ConsoleLog.Write(
                    "CardRefund",
                    $"payment view card refund tender failed statusKey={result.StatusKey} message={LogValue(result.StatusMessage)}");
            }

            if (method == PaymentMethodKind.Card)
            {
                ShowOverlayIfTerminalError(result);
            }

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
        SetStatus(result.StatusKey, result.StatusMessage);
        NotifyPaymentCommandStates();
        if (method == PaymentMethodKind.Card &&
            !cardPaymentWasManuallyCancelled &&
            IsSettlementComplete() &&
            (IsPaymentMode || IsRefundMode))
        {
            // 全额卡支付或全额退卡已由终端批准，立即落单，后续主窗口沿用 CardAuto 小票打印。
            await CompletePaymentFromTendersAsync();
        }
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

        if (TrySetOfflineVoucherRefundTenderStatus())
        {
            return;
        }

        if (!IsZeroSettlementMode && PaymentTenders.Count == 0)
        {
            SetStatus(GetNoTendersStatusKey());
            return;
        }

        if (!IsSettlementComplete())
        {
            SetStatus(GetIncompleteSettlementStatusKey());
            return;
        }

        await CompletePaymentFromTendersAsync();
    }

    private async Task CompletePaymentFromTendersAsync()
    {
        if (TrySetOfflineVoucherRefundTenderStatus())
        {
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

    private bool CanAddTender(PaymentMethodKind method, bool allowDefaultAmount)
    {
        if (IsPaymentInteractionLocked ||
            _activeCardPaymentCts is not null ||
            _pendingVoucherUploadOrderGuid is not null ||
            _cart.IsEmpty || _cart.HasNonIntegerQuantity || _cart.HasZeroPriceLine || IsZeroSettlementMode)
        {
            return false;
        }

        if (HasTenderForMethod(method) && !(IsRefundMode && method == PaymentMethodKind.Card))
        {
            return false;
        }

        var amountText = ResolveTenderAmountText(method, allowDefaultAmount);
        if (!_workflowService.TryParseTenderedAmount(amountText, out var amount) || amount <= 0m)
        {
            return false;
        }

        var remainingAmount = IsRefundMode
            ? GetRefundRemainingAmount(method)
            : method == PaymentMethodKind.Cash
                ? GetCashRemainingAmount()
                : GetExternalRemainingAmount();
        if (remainingAmount <= 0m)
        {
            return false;
        }

        if (IsRefundMode &&
            method == PaymentMethodKind.Card &&
            string.IsNullOrWhiteSpace(GetRefundReference(method)))
        {
            return false;
        }

        if (!IsRefundMode &&
            method == PaymentMethodKind.Voucher &&
            !allowDefaultAmount &&
            string.IsNullOrWhiteSpace(VoucherCodeText))
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
            !_cart.HasZeroPriceLine &&
            (IsZeroSettlementMode || PaymentTenders.Count > 0) &&
            IsSettlementComplete();
    }

    private void RefreshCartValidationStatus()
    {
        if (TrySetBlockingCartIssueStatus())
        {
            return;
        }

        if (_statusTextOverride is null &&
            (_statusKey is "cart.status.quantityMustBeInteger" or "cart.status.zeroPriceItem" || IsModeStatusKey(_statusKey)))
        {
            SetStatus(GetReadyStatusKey());
        }
    }

    private bool TrySetBlockingCartIssueStatus()
    {
        if (_cart.HasNonIntegerQuantity)
        {
            SetStatus("cart.status.quantityMustBeInteger");
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
        _workflowRemainingAmount = _workflowService.CalculateRemainingAmount(ActualAmount, PaymentTenders.ToList());
        RemainingAmount = Math.Abs(_workflowRemainingAmount);
        ChangeDue = IsPaymentMode &&
            PaymentTenders.Count == 0 &&
            _workflowService.TryParseTenderedAmount(TenderAmountText, out var tenderedAmount)
                ? Math.Max(0m, CashRoundingPolicy.CalculateCashChange(ActualAmount, [], tenderedAmount))
                : IsPaymentMode
                    ? _workflowService.CalculateChange(PaymentTenders.ToList(), ActualAmount)
                    : 0m;
        OnPropertyChanged(nameof(QuickCashAmounts));
    }

    private IReadOnlyList<QuickCashOption> BuildQuickCashAmounts()
    {
        return
        [
            new QuickCashOption(100m, "$100", "#FF2F9E6D", "White"),
            new QuickCashOption(50m, "$50", "#FFF2C94C", "#FF2E1500"),
            new QuickCashOption(20m, "$20", "#FFE45858", "White"),
            new QuickCashOption(10m, "$10", "#FF2E6BB8", "White"),
            new QuickCashOption(5m, "$5", "#FFC15AA1", "White")
        ];
    }

    private void SyncTenderAmountToRemaining(bool force = false)
    {
        if (!force && !string.IsNullOrWhiteSpace(TenderAmountText))
        {
            return;
        }

        var amount = SelectedPaymentMethod == PaymentMethodKind.Cash
            ? GetCashRemainingAmount()
            : IsRefundMode
                ? GetRefundRemainingAmount(SelectedPaymentMethod)
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

        return ResolveDefaultTenderAmountText(method);
    }

    private string ResolveDefaultTenderAmountText(PaymentMethodKind method)
    {
        var amount = method == PaymentMethodKind.Cash
            ? GetCashRemainingAmount()
            : IsRefundMode
                ? GetRefundRemainingAmount(method)
                : GetExternalRemainingAmount();
        return amount > 0m ? amount.ToString("0.00") : string.Empty;
    }

    private decimal GetExternalRemainingAmount()
    {
        var tenderedAmount = PaymentTenders.Sum(tender => tender.Amount);
        return Math.Abs(decimal.Round(ActualAmount - tenderedAmount, 2, MidpointRounding.AwayFromZero));
    }

    private bool HasTenderForMethod(PaymentMethodKind method)
    {
        return PaymentTenders.Any(tender => tender.Method == method);
    }

    private bool IsOfflineVoucherRefundUnavailable(PaymentMethodKind method)
    {
        // 退款代金券需要在线发券，离线时提前拦截，避免进入待上传状态后卡住。
        return IsRefundMode &&
            method == PaymentMethodKind.Voucher &&
            !Session.IsOnline;
    }

    private bool HasOfflineVoucherRefundTender()
    {
        return IsRefundMode &&
            !Session.IsOnline &&
            PaymentTenders.Any(IsVoucherRefundTender);
    }

    private static bool IsVoucherRefundTender(PaymentTender tender)
    {
        return tender.Method == PaymentMethodKind.Voucher && tender.Amount < 0m;
    }

    private bool TrySetOfflineVoucherRefundTenderStatus()
    {
        if (!HasOfflineVoucherRefundTender())
        {
            return false;
        }

        SetOfflineVoucherRefundUnavailableStatus();
        return true;
    }

    private void SetOfflineVoucherRefundUnavailableStatus()
    {
        SetStatus("payment.refund.status.voucherOfflineUnavailable");
        NotifyPaymentCommandStates();
    }

    private void BackToPos()
    {
        if (TrySetOfflineVoucherRefundTenderStatus())
        {
            return;
        }

        if (PaymentTenders.Count > 0)
        {
            SetStatus("payment.status.removeTendersBeforeBack");
            NotifyPaymentCommandStates();
            return;
        }

        _onBackToPos?.Invoke();
    }

    private bool CanBackToPos()
    {
        return !IsPaymentInteractionLocked &&
            !IsCardPaymentInProgress &&
            !_awaitingLateCardResultAfterManualCancel;
    }

    private void ShowInstallmentCenter()
    {
        // 分期流程暂时独立于现有收款流程，只负责跳转到新骨架页面。
        _onShowInstallmentCenter?.Invoke();
    }

    private bool CanShowInstallmentCenter()
    {
        return IsPaymentMode &&
            !IsPaymentInteractionLocked &&
            _activeCardPaymentCts is null &&
            !_cart.IsEmpty;
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
        }
    }

    private bool CanCancelPayment()
    {
        return IsCardPaymentInProgress ||
            _awaitingLateCardResultAfterManualCancel;
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

        // 先清掉活动 CTS，再解锁并刷新命令；按钮可用性会检查 _activeCardPaymentCts 是否为空。
        var activeCardPaymentCts = _activeCardPaymentCts;
        _activeCardPaymentCts = null;
        if (ReferenceEquals(_manuallyCancelledCardPaymentCts, cardPaymentCts))
        {
            _manuallyCancelledCardPaymentCts = null;
        }

        _cardPaymentCancellationRequested = false;
        IsCardPaymentInProgress = false;
        IsPaymentInteractionLocked = false;
        activeCardPaymentCts?.Dispose();
        NotifyPaymentCommandStates();
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

    private PaymentEntryMode CalculatePaymentMode()
    {
        if (ActualAmount < 0m)
        {
            return PaymentEntryMode.Refund;
        }

        if (ActualAmount == 0m)
        {
            return PaymentEntryMode.ZeroSettlement;
        }

        return PaymentEntryMode.Payment;
    }

    private decimal GetCashRemainingAmount()
    {
        return IsRefundMode
            ? new CashRoundingPolicy().NormalizeCashTender(GetRefundRemainingAmount(PaymentMethodKind.Cash))
            : CashRoundingPolicy.GetCashPayableAmount(ActualAmount, PaymentTenders.ToList());
    }

    private decimal GetRefundRemainingAmount(PaymentMethodKind method)
    {
        var netRemaining = Math.Abs(decimal.Round(_workflowRemainingAmount, 2, MidpointRounding.AwayFromZero));
        if (netRemaining <= 0m)
        {
            return 0m;
        }

        if (method != PaymentMethodKind.Card)
        {
            return netRemaining;
        }

        var nextCardCapacity = GetNextCardRefundCapacity();
        return nextCardCapacity is null
            ? 0m
            : Math.Min(netRemaining, nextCardCapacity.Value.RemainingAmount);
    }

    private string? GetRefundReference(PaymentMethodKind method)
    {
        if (!IsRefundMode)
        {
            return null;
        }

        return method == PaymentMethodKind.Card
            ? GetNextCardRefundCapacity()?.Reference
            : null;
    }

    private (string Reference, decimal RemainingAmount)? GetNextCardRefundCapacity()
    {
        foreach (var capacity in _cart.ReturnPaymentCapacities.Where(capacity => capacity.Method == PaymentMethodKind.Card))
        {
            var reference = ResolveOriginalCardRefundReference(capacity);
            ConsoleLog.Write(
                "CardRefund",
                $"capacity candidate originalOrder={capacity.OriginalOrderGuid?.ToString() ?? "<null>"} " +
                $"remaining={capacity.RemainingAmount:0.00} rawReference={LogValue(capacity.Reference)} " +
                $"resolvedReference={LogValue(reference)} cardTxCount={capacity.CardTransactions?.Count ?? 0} " +
                $"refundReferences={LogValue(FormatRefundReferences(capacity.CardTransactions))}");
            if (reference is null)
            {
                continue;
            }

            var existingTendered = Math.Abs(PaymentTenders
                .Where(tender => tender.Method == PaymentMethodKind.Card)
                .Where(tender => string.Equals(GetOriginalCardReference(tender.Reference), reference, StringComparison.OrdinalIgnoreCase))
                .Sum(tender => tender.Amount));
            var remainingCapacity = Math.Max(0m, capacity.RemainingAmount - existingTendered);
            var remainingReturnAmount = GetRemainingReturnAmountForCardCapacity(capacity);
            var remainingAmount = remainingReturnAmount is decimal orderLimitedAmount
                ? Math.Min(remainingCapacity, orderLimitedAmount)
                : remainingCapacity;
            ConsoleLog.Write(
                "CardRefund",
                $"capacity evaluated originalReference={LogValue(reference)} existingTendered={existingTendered:0.00} " +
                $"remainingCapacity={remainingCapacity:0.00} orderLimited={remainingReturnAmount?.ToString("0.00") ?? "<null>"} " +
                $"selectedRemaining={remainingAmount:0.00}");
            if (remainingAmount > 0m)
            {
                ConsoleLog.Write(
                    "CardRefund",
                    $"capacity selected originalReference={LogValue(reference)} remaining={remainingAmount:0.00}");
                return (reference, remainingAmount);
            }
        }

        ConsoleLog.Write("CardRefund", "no eligible card refund capacity selected");
        return null;
    }

    private decimal? GetRemainingReturnAmountForCardCapacity(OrderReturnPaymentCapacityDto capacity)
    {
        if (capacity.OriginalOrderGuid is not Guid originalOrderGuid)
        {
            return null;
        }

        var returnAmount = Math.Abs(decimal.Round(
            _cart.Lines
                .Where(line => line.IsReturnLine && line.OriginalOrderGuid == originalOrderGuid)
                .Sum(line => line.ActualAmount),
            2,
            MidpointRounding.AwayFromZero));
        if (returnAmount <= 0m)
        {
            return 0m;
        }

        var existingCardRefundsForOrder = Math.Abs(decimal.Round(
            PaymentTenders
                .Where(tender => tender.Method == PaymentMethodKind.Card)
                .Where(tender => IsCardRefundForOriginalOrder(tender.Reference, originalOrderGuid))
                .Sum(tender => tender.Amount),
            2,
            MidpointRounding.AwayFromZero));
        return Math.Max(0m, returnAmount - existingCardRefundsForOrder);
    }

    private bool IsCardRefundForOriginalOrder(string? reference, Guid originalOrderGuid)
    {
        var originalReference = GetOriginalCardReference(reference);
        if (originalReference is null)
        {
            return false;
        }

        return _cart.ReturnPaymentCapacities.Any(capacity =>
            capacity.Method == PaymentMethodKind.Card &&
            capacity.OriginalOrderGuid == originalOrderGuid &&
            string.Equals(NormalizeReference(capacity.Reference), originalReference, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetOriginalCardReference(string? reference)
    {
        return CardRefundReference.TryGetOriginalReference(reference, out var originalReference)
            ? NormalizeReference(originalReference)
            : NormalizeReference(reference);
    }

    private static string? ResolveOriginalCardRefundReference(OrderReturnPaymentCapacityDto capacity)
    {
        var reference = NormalizeReference(capacity.Reference);
        if (reference is null || RequiresLinklyRefundReference(reference))
        {
            return BuildLinklyRefundReference(capacity.CardTransactions, reference) ?? reference;
        }

        return reference;
    }

    private static bool RequiresLinklyRefundReference(string reference)
    {
        return reference.StartsWith("ANZCLOUD:", StringComparison.OrdinalIgnoreCase) ||
            reference.StartsWith($"{LinklyBackendPaymentReference.Prefix}:", StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildLinklyRefundReference(
        IReadOnlyList<CardTransactionDto>? cardTransactions,
        string? fallbackReference)
    {
        foreach (var transaction in cardTransactions ?? [])
        {
            var refundReference = NormalizeReference(transaction.RefundReference);
            if (refundReference is null)
            {
                continue;
            }

            var txnRef = NormalizeReference(transaction.TxnRef) ??
                TryGetLinklyTxnRef(fallbackReference) ??
                "RFN";
            return $"ANZCLOUD:{txnRef}:{refundReference}";
        }

        return null;
    }

    private static string FormatRefundReferences(IReadOnlyList<CardTransactionDto>? cardTransactions)
    {
        var values = (cardTransactions ?? [])
            .Select(transaction => transaction.RefundReference)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 0 ? string.Empty : string.Join(',', values);
    }

    private static string? TryGetLinklyTxnRef(string? reference)
    {
        var parts = reference?.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        return parts.Length >= 2 &&
            (string.Equals(parts[0], "ANZCLOUD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parts[0], LinklyBackendPaymentReference.Prefix, StringComparison.OrdinalIgnoreCase))
                ? parts[1]
                : null;
    }

    private static string? NormalizeReference(string? reference)
    {
        return string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private bool IsSettlementComplete()
    {
        return PaymentMode switch
        {
            PaymentEntryMode.Refund => _workflowRemainingAmount >= 0m,
            PaymentEntryMode.ZeroSettlement => true,
            _ => _workflowRemainingAmount <= 0m
        };
    }

    private bool HasBlockingCartIssue()
    {
        return _cart.HasNonIntegerQuantity || _cart.HasZeroPriceLine;
    }

    private string GetScreenTitleKey()
    {
        return PaymentMode switch
        {
            PaymentEntryMode.Refund => "payment.refund.title",
            PaymentEntryMode.ZeroSettlement => "payment.zeroSettlement.title",
            _ => "payment.title"
        };
    }

    private string GetRemainingAmountKey()
    {
        return IsRefundMode ? "payment.refund.remaining" : "payment.remaining";
    }

    private string GetConfirmPaymentKey()
    {
        return PaymentMode switch
        {
            PaymentEntryMode.Refund => "payment.refund.confirm",
            PaymentEntryMode.ZeroSettlement => "payment.zeroSettlement.confirm",
            _ => "payment.confirm"
        };
    }

    private string GetNoTendersKey()
    {
        return PaymentMode switch
        {
            PaymentEntryMode.Refund => "payment.refund.noTenders",
            PaymentEntryMode.ZeroSettlement => "payment.zeroSettlement.noTenders",
            _ => "payment.noTenders"
        };
    }

    private string GetReadyStatusKey()
    {
        return PaymentMode switch
        {
            PaymentEntryMode.Refund => "payment.refund.status.ready",
            PaymentEntryMode.ZeroSettlement => "payment.zeroSettlement.status.ready",
            _ => "payment.status.ready"
        };
    }

    private string GetNoTendersStatusKey()
    {
        return IsRefundMode ? "payment.refund.status.noTendersAdded" : "payment.status.noTendersAdded";
    }

    private string GetIncompleteSettlementStatusKey()
    {
        return IsRefundMode ? "payment.refund.status.remainingBalance" : "payment.status.remainingBalance";
    }

    private bool IsModeStatusKey(string statusKey)
    {
        return statusKey == "payment.status.ready" ||
            statusKey == "payment.refund.status.ready" ||
            statusKey == "payment.zeroSettlement.status.ready";
    }

    private void NotifyPaymentCommandStates()
    {
        NumberInputCommand.NotifyCanExecuteChanged();
        SelectCashCommand.NotifyCanExecuteChanged();
        SelectCardCommand.NotifyCanExecuteChanged();
        SelectVoucherCommand.NotifyCanExecuteChanged();
        QuickCashCommand.NotifyCanExecuteChanged();
        RemoveTenderCommand.NotifyCanExecuteChanged();
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        BackToPosCommand.NotifyCanExecuteChanged();
        ShowInstallmentCenterCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsCancelPaymentVisible));
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
        OnPropertyChanged(nameof(ConfirmPaymentText));
        OnPropertyChanged(nameof(NoTendersText));
        OnPropertyChanged(nameof(CashMethodText));
        OnPropertyChanged(nameof(CardMethodText));
        OnPropertyChanged(nameof(VoucherMethodText));
        OnPropertyChanged(nameof(InstallmentMethodText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void CloseCardPaymentErrorOverlay()
    {
        if (CardPaymentErrorOverlay is not null)
        {
            CardPaymentErrorOverlay.IsOpen = false;
        }

        CardPaymentErrorPrimaryActionCommand.NotifyCanExecuteChanged();
    }

    private bool CanExecuteCardPaymentErrorPrimaryAction()
    {
        return CardPaymentErrorOverlay is { IsOpen: true, HasPrimaryAction: true } &&
            _recoverPreviousCardTransactionAsync is not null;
    }

    private async Task ExecuteCardPaymentErrorPrimaryActionAsync()
    {
        if (!CanExecuteCardPaymentErrorPrimaryAction() || _recoverPreviousCardTransactionAsync is null)
        {
            return;
        }

        SetStatus("payment.card.error.overlay.activeSession.recovering");
        await _recoverPreviousCardTransactionAsync();
        if (CardPaymentErrorOverlay is not null)
        {
            CardPaymentErrorOverlay.IsOpen = false;
        }

        CardPaymentErrorPrimaryActionCommand.NotifyCanExecuteChanged();
    }

    private static CardPaymentErrorOverlayViewModel? ClassifyCardPaymentError(
        string statusKey,
        string? statusMessage)
    {
        var overlay = ClassifyCardPaymentErrorByStatusKey(statusKey);
        if (overlay is not null)
        {
            return overlay;
        }

        if (string.IsNullOrWhiteSpace(statusMessage))
            return null;

        var message = statusMessage;

        if (message.Contains("unfinished card transaction", StringComparison.OrdinalIgnoreCase))
            return CardPaymentErrorOverlayViewModel.ActiveSessionRequiresRecovery();

        if (message.Contains("connection failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("connection was closed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("could not be sent", StringComparison.OrdinalIgnoreCase))
            return CardPaymentErrorOverlayViewModel.ConnectionFailed();

        if (message.Contains("communication failed", StringComparison.OrdinalIgnoreCase))
        {
            if (message.Contains("Square", StringComparison.OrdinalIgnoreCase))
                return CardPaymentErrorOverlayViewModel.SquareCommunicationFailed();
            return CardPaymentErrorOverlayViewModel.CloudCommunicationFailed();
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return CardPaymentErrorOverlayViewModel.Timeout();

        if (message.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
            return CardPaymentErrorOverlayViewModel.ConnectionFailed();

        return null;
    }

    private static CardPaymentErrorOverlayViewModel? ClassifyCardPaymentErrorByStatusKey(string statusKey)
    {
        return statusKey switch
        {
            "linkly.local.connectionFailed" or
            "payment.card.linklyUnavailable" => CardPaymentErrorOverlayViewModel.ConnectionFailed(),

            "linkly.cloud.communicationFailed" or
            "linkly.backend.communicationFailed" => CardPaymentErrorOverlayViewModel.CloudCommunicationFailed(),

            "linkly.backend.activeSessionRequiresRecovery" => CardPaymentErrorOverlayViewModel.ActiveSessionRequiresRecovery(),

            "payment.card.squareCommunicationFailed" => CardPaymentErrorOverlayViewModel.SquareCommunicationFailed(),

            "linkly.local.timeout" or
            "linkly.cloud.timeout" or
            "linkly.backend.timeout" => CardPaymentErrorOverlayViewModel.Timeout(),

            _ => null
        };
    }

    private void ShowOverlayIfTerminalError(PaymentTenderAttemptResult result)
    {
        var overlay = ClassifyCardPaymentError(result.StatusKey, result.StatusMessage);
        if (overlay is null)
            return;

        overlay.IsOpen = true;
        CardPaymentErrorOverlay = overlay;
        CardPaymentErrorPrimaryActionCommand.NotifyCanExecuteChanged();
    }
}

public sealed record QuickCashOption(decimal Amount, string Label, string NoteColorKey, string ForegroundColorKey);

public enum PaymentEntryMode
{
    Payment,
    Refund,
    ZeroSettlement
}
