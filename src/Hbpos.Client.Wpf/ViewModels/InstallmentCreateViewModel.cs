using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class InstallmentCreateViewModel : ObservableObject
{
    private const decimal MinimumDownPaymentAmount = 20m;

    private readonly IInstallmentOrderService _installmentOrderService;
    private readonly Func<InstallmentOrderSummary, Task> _onCreatedAsync;
    private readonly Action _backToCenter;
    private readonly ILocalizationService? _localization;
    private string? _statusResourceKey;
    private string _statusFallback = string.Empty;
    private object[] _statusResourceArgs = [];

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private PosCartServiceSnapshot? _cartSnapshot;

    [ObservableProperty]
    private string _customerName = string.Empty;

    [ObservableProperty]
    private string _customerPhone = string.Empty;

    [ObservableProperty]
    private decimal _downPaymentAmount;

    [ObservableProperty]
    private PaymentMethodKind _downPaymentMethod = PaymentMethodKind.Cash;

    [ObservableProperty]
    private string _downPaymentReference = string.Empty;

    [ObservableProperty]
    private string _voucherReservationToken = string.Empty;

    [ObservableProperty]
    private string _note = string.Empty;

    [ObservableProperty]
    private bool _isSubmitting;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public InstallmentCreateViewModel(
        IInstallmentOrderService installmentOrderService,
        PosSessionState session,
        Func<InstallmentOrderSummary, Task> onCreatedAsync,
        Action backToCenter,
        ILocalizationService? localization = null)
    {
        _installmentOrderService = installmentOrderService;
        _session = session;
        _onCreatedAsync = onCreatedAsync;
        _backToCenter = backToCenter;
        _localization = localization;
        if (_localization is not null)
        {
            _localization.CultureChanged += (_, _) => RaiseLocalizedProperties();
        }

        SubmitCommand = new AsyncRelayCommand(SubmitAsync, CanSubmit);
        BackToCenterCommand = new RelayCommand(BackToCenter);
        RefreshPaymentMethodOptions();
        SetStatusResource("installment.create.status.ready", "Complete the customer, down payment, and installment details.");
    }

    public ObservableCollection<PosCartLineServiceSnapshot> CartLines { get; } = [];

    public IAsyncRelayCommand SubmitCommand { get; }

    public IRelayCommand BackToCenterCommand { get; }

    public ObservableCollection<InstallmentPaymentMethodOption> PaymentMethodOptions { get; } = [];

    public string PageTitleText => T("installment.create.title", "Create Installment");

    public string BackToCenterText => T("installment.create.action.back", "Back to Installment Center");

    public string SubmitText => T("installment.create.action.submit", "Create Installment Order");

    public string OfflineNoticeText => T("installment.create.offline", "Installment orders cannot be created while offline.");

    public string CustomerSectionText => T("installment.create.section.customer", "Customer Information");

    public string CartSectionText => T("installment.create.section.cart", "Cart Details");

    public string PaymentSectionText => T("installment.create.section.payment", "Down Payment");

    public string OrderTotalLabelText => T("installment.create.order.total", "Order Total");

    public string FinancedAmountText => string.Format(GetCulture(), T("installment.create.order.financed", "Financed {0:C2}"), FinancedAmount);

    public string DownPaymentMethodText => PaymentMethodOptions
        .FirstOrDefault(option => option.Method == DownPaymentMethod)?.DisplayName ?? DownPaymentMethod.ToString();

    public bool IsOffline => !Session.IsOnline;

    public bool IsVoucherPaymentSelected => DownPaymentMethod == PaymentMethodKind.Voucher;

    public decimal GoodsAmount => CartSnapshot?.TotalAmount ?? 0m;

    public decimal DiscountAmount => CartSnapshot?.DiscountAmount ?? 0m;

    public decimal TotalAmount => CartSnapshot?.ActualAmount ?? 0m;

    public decimal FinancedAmount => Math.Max(0m, TotalAmount - DownPaymentAmount);

    public string DownPaymentStatusText => BuildDownPaymentStatusText();

    public bool IsSubmitEnabled => CanSubmit();

    partial void OnSessionChanged(PosSessionState value)
    {
        RaiseActionStateChanged();
    }

    partial void OnCartSnapshotChanged(PosCartServiceSnapshot? value)
    {
        CartLines.ReplaceWith(value?.Lines ?? []);
        if (value is not null && DownPaymentAmount > value.ActualAmount)
        {
            DownPaymentAmount = value.ActualAmount;
        }

        RaiseAmountStateChanged();
        RaiseActionStateChanged();
    }

    partial void OnDownPaymentAmountChanged(decimal value)
    {
        if (value < 0m)
        {
            DownPaymentAmount = 0m;
            return;
        }

        if (TotalAmount > 0m && value > TotalAmount)
        {
            DownPaymentAmount = TotalAmount;
            return;
        }

        RaiseAmountStateChanged();
        RaiseActionStateChanged();
    }

    partial void OnDownPaymentMethodChanged(PaymentMethodKind value)
    {
        if (value != PaymentMethodKind.Voucher)
        {
            VoucherReservationToken = string.Empty;
        }

        OnPropertyChanged(nameof(IsVoucherPaymentSelected));
        OnPropertyChanged(nameof(DownPaymentMethodText));
        RaiseActionStateChanged();
    }

    partial void OnDownPaymentReferenceChanged(string value)
    {
        RaiseActionStateChanged();
    }

    partial void OnVoucherReservationTokenChanged(string value)
    {
        RaiseActionStateChanged();
    }

    partial void OnCustomerNameChanged(string value)
    {
        RaiseActionStateChanged();
    }

    partial void OnCustomerPhoneChanged(string value)
    {
        RaiseActionStateChanged();
    }

    partial void OnIsSubmittingChanged(bool value)
    {
        RaiseActionStateChanged();
    }

    public void Prepare(PosSessionState session, PosCartServiceSnapshot? cartSnapshot)
    {
        Session = session;
        CartSnapshot = cartSnapshot;
        DownPaymentMethod = PaymentMethodKind.Cash;
        DownPaymentReference = string.Empty;
        VoucherReservationToken = string.Empty;
        DownPaymentAmount = CalculateDefaultDownPayment(cartSnapshot?.ActualAmount ?? 0m);
        CustomerName = string.Empty;
        CustomerPhone = string.Empty;
        Note = string.Empty;
        if (cartSnapshot is null)
        {
            SetStatusResource("installment.create.status.missingCart", "There is no current order available for installment creation.");
        }
        else
        {
            SetStatusResource("installment.create.status.ready", "Complete the customer, down payment, and installment details.");
        }

        RaiseAmountStateChanged();
        RaiseActionStateChanged();
    }

    private async Task SubmitAsync()
    {
        if (CartSnapshot is null)
        {
            SetStatusResource("installment.create.status.missingCart", "There is no current order available for installment creation.");
            return;
        }

        IsSubmitting = true;
        try
        {
            // ViewModel 只收集 UI 输入，请求对象由客户端服务统一落地并提交。
            var request = new InstallmentOrderCreateRequest(
                Session,
                CartSnapshot,
                CustomerName.Trim(),
                CustomerPhone.Trim(),
                DownPaymentAmount,
                new InstallmentPaymentDraft(
                    Guid.NewGuid(),
                    DownPaymentMethod,
                    DownPaymentAmount,
                    NormalizeOptional(DownPaymentReference),
                    NormalizeOptional(VoucherReservationToken)),
                Note.Trim());
            var result = await _installmentOrderService.CreateOrderAsync(request);
            SetLiteralStatus(result.Message);
            if (result.Succeeded && result.Order is not null)
            {
                await _onCreatedAsync(result.Order);
            }
        }
        catch (Exception ex)
        {
            SetLiteralStatus(ex.Message);
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private bool CanSubmit()
    {
        return !IsSubmitting &&
            !IsOffline &&
            CartSnapshot is not null &&
            CartSnapshot.Lines.Count > 0 &&
            TotalAmount > 0m &&
            !string.IsNullOrWhiteSpace(CustomerName) &&
            !string.IsNullOrWhiteSpace(CustomerPhone) &&
            IsValidDownPayment() &&
            (DownPaymentMethod != PaymentMethodKind.Voucher ||
             (!string.IsNullOrWhiteSpace(DownPaymentReference) && !string.IsNullOrWhiteSpace(VoucherReservationToken)));
    }

    private bool IsValidDownPayment()
    {
        if (DownPaymentAmount <= 0m || DownPaymentAmount > TotalAmount)
        {
            return false;
        }

        return TotalAmount < MinimumDownPaymentAmount
            ? DownPaymentAmount == TotalAmount
            : DownPaymentAmount >= MinimumDownPaymentAmount;
    }

    private void BackToCenter()
    {
        _backToCenter();
    }

    private void RaiseAmountStateChanged()
    {
        OnPropertyChanged(nameof(GoodsAmount));
        OnPropertyChanged(nameof(DiscountAmount));
        OnPropertyChanged(nameof(TotalAmount));
        OnPropertyChanged(nameof(FinancedAmount));
        OnPropertyChanged(nameof(FinancedAmountText));
        OnPropertyChanged(nameof(DownPaymentMethodText));
        OnPropertyChanged(nameof(DownPaymentStatusText));
    }

    private void RaiseActionStateChanged()
    {
        SubmitCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(IsSubmitEnabled));
        OnPropertyChanged(nameof(IsVoucherPaymentSelected));
        OnPropertyChanged(nameof(DownPaymentMethodText));
        OnPropertyChanged(nameof(DownPaymentStatusText));
    }

    private void RaiseLocalizedProperties()
    {
        RefreshPaymentMethodOptions();
        if (_statusResourceKey is not null)
        {
            StatusMessage = FormatResource(_statusResourceKey, _statusFallback, _statusResourceArgs);
        }

        OnPropertyChanged(nameof(PageTitleText));
        OnPropertyChanged(nameof(BackToCenterText));
        OnPropertyChanged(nameof(SubmitText));
        OnPropertyChanged(nameof(OfflineNoticeText));
        OnPropertyChanged(nameof(CustomerSectionText));
        OnPropertyChanged(nameof(CartSectionText));
        OnPropertyChanged(nameof(PaymentSectionText));
        OnPropertyChanged(nameof(OrderTotalLabelText));
        OnPropertyChanged(nameof(FinancedAmountText));
        OnPropertyChanged(nameof(DownPaymentMethodText));
        OnPropertyChanged(nameof(DownPaymentStatusText));
    }

    private string BuildDownPaymentStatusText()
    {
        if (IsOffline)
        {
            return T("installment.create.payment.status.offline", "Offline mode cannot submit a down payment.");
        }

        return DownPaymentMethod switch
        {
            PaymentMethodKind.Cash => T("installment.create.payment.status.cash", "Cash down payment does not require an additional reference."),
            PaymentMethodKind.Card => string.IsNullOrWhiteSpace(DownPaymentReference)
                ? T("installment.create.payment.status.card.empty", "Card down payment may include a transaction reference for reconciliation.")
                : T("installment.create.payment.status.card.ready", "Card down payment reference is ready."),
            PaymentMethodKind.Voucher when string.IsNullOrWhiteSpace(DownPaymentReference) =>
                T("installment.create.payment.status.voucher.missingCode", "Enter a voucher code."),
            PaymentMethodKind.Voucher when string.IsNullOrWhiteSpace(VoucherReservationToken) =>
                T("installment.create.payment.status.voucher.missingToken", "Enter the voucher reservation token."),
            PaymentMethodKind.Voucher =>
                T("installment.create.payment.status.voucher.ready", "Voucher code and reservation token are ready."),
            _ => string.Empty
        };
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

    private static decimal CalculateDefaultDownPayment(decimal orderAmount)
    {
        if (orderAmount <= 0m)
        {
            return 0m;
        }

        return orderAmount < MinimumDownPaymentAmount ? orderAmount : MinimumDownPaymentAmount;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
}

public sealed record InstallmentPaymentMethodOption(PaymentMethodKind Method, string DisplayName);
