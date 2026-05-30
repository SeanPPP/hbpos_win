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
        PaymentMethodOptions =
        [
            new InstallmentPaymentMethodOption(PaymentMethodKind.Cash, "现金"),
            new InstallmentPaymentMethodOption(PaymentMethodKind.Card, "银行卡"),
            new InstallmentPaymentMethodOption(PaymentMethodKind.Voucher, "代金券")
        ];
        StatusMessage = T("installment.create.status.ready", "请完善客户、首付和分期信息。");
    }

    public ObservableCollection<PosCartLineServiceSnapshot> CartLines { get; } = [];

    public IAsyncRelayCommand SubmitCommand { get; }

    public IRelayCommand BackToCenterCommand { get; }

    public IReadOnlyList<InstallmentPaymentMethodOption> PaymentMethodOptions { get; }

    public string PageTitleText => T("installment.create.title", "创建分期");

    public string BackToCenterText => T("installment.create.action.back", "返回分期中心");

    public string SubmitText => T("installment.create.action.submit", "创建分期单");

    public string OfflineNoticeText => T("installment.create.offline", "离线时不能创建分期单。");

    public string CustomerSectionText => T("installment.create.section.customer", "客户信息");

    public string CartSectionText => T("installment.create.section.cart", "购物车明细");

    public string PaymentSectionText => T("installment.create.section.payment", "首付支付");

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
        StatusMessage = cartSnapshot is null
            ? T("installment.create.status.missingCart", "当前没有可用于创建分期的订单。")
            : T("installment.create.status.ready", "请完善客户、首付和分期信息。");
        RaiseAmountStateChanged();
        RaiseActionStateChanged();
    }

    private async Task SubmitAsync()
    {
        if (CartSnapshot is null)
        {
            StatusMessage = T("installment.create.status.missingCart", "当前没有可用于创建分期的订单。");
            return;
        }

        IsSubmitting = true;
        try
        {
            // ViewModel 只负责收集 UI 输入，请求对象由客户端服务统一落地。
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
            StatusMessage = result.Message;
            if (result.Succeeded && result.Order is not null)
            {
                await _onCreatedAsync(result.Order);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
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
        OnPropertyChanged(nameof(PageTitleText));
        OnPropertyChanged(nameof(BackToCenterText));
        OnPropertyChanged(nameof(SubmitText));
        OnPropertyChanged(nameof(OfflineNoticeText));
        OnPropertyChanged(nameof(CustomerSectionText));
        OnPropertyChanged(nameof(CartSectionText));
        OnPropertyChanged(nameof(PaymentSectionText));
        OnPropertyChanged(nameof(DownPaymentMethodText));
        OnPropertyChanged(nameof(DownPaymentStatusText));
    }

    private string BuildDownPaymentStatusText()
    {
        if (IsOffline)
        {
            return T("installment.create.payment.status.offline", "当前为离线状态，暂不能提交首付。");
        }

        return DownPaymentMethod switch
        {
            PaymentMethodKind.Cash => T("installment.create.payment.status.cash", "现金首付无需额外凭证。"),
            PaymentMethodKind.Card => string.IsNullOrWhiteSpace(DownPaymentReference)
                ? T("installment.create.payment.status.card.empty", "银行卡首付可填写交易流水号，便于对账。")
                : T("installment.create.payment.status.card.ready", "银行卡首付流水号已填写。"),
            PaymentMethodKind.Voucher when string.IsNullOrWhiteSpace(DownPaymentReference) =>
                T("installment.create.payment.status.voucher.missingCode", "请输入代金券券码。"),
            PaymentMethodKind.Voucher when string.IsNullOrWhiteSpace(VoucherReservationToken) =>
                T("installment.create.payment.status.voucher.missingToken", "请输入代金券锁定令牌。"),
            PaymentMethodKind.Voucher =>
                T("installment.create.payment.status.voucher.ready", "代金券券码和锁定令牌已准备完成。"),
            _ => string.Empty
        };
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
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }
}

public sealed record InstallmentPaymentMethodOption(PaymentMethodKind Method, string DisplayName);
