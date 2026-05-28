using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface ICashPaymentWorkflowService
{
    bool TryParseTenderedAmount(string? amountTenderedText, out decimal tenderedAmount);

    decimal CalculateChange(string? amountTenderedText, decimal actualAmount);

    decimal CalculateTenderedAmount(IReadOnlyList<PaymentTender> tenders);

    decimal CalculateRemainingAmount(decimal actualAmount, IReadOnlyList<PaymentTender> tenders);

    decimal CalculateChange(IReadOnlyList<PaymentTender> tenders, decimal actualAmount);

    Task<PaymentTenderAttemptResult> AddTenderAsync(
        PaymentMethodKind method,
        PosSessionState session,
        decimal actualAmount,
        IReadOnlyList<PaymentTender> currentTenders,
        string? amountText,
        string? referenceText = null,
        CancellationToken cancellationToken = default);

    Task<CashPaymentWorkflowResult> CompleteAsync(
        PosCartService cart,
        PosSessionState session,
        string? amountTenderedText,
        CancellationToken cancellationToken = default);

    Task<CashPaymentWorkflowResult> CompletePaymentAsync(
        PosCartService cart,
        PosSessionState session,
        IReadOnlyList<PaymentTender> tenders,
        decimal cashTenderedAmount,
        CancellationToken cancellationToken = default);

    Task<CashPaymentWorkflowResult> RetryVoucherUploadAsync(
        Guid orderGuid,
        PosCartService cart,
        PosSessionState session,
        decimal tenderedAmount,
        decimal changeAmount,
        CancellationToken cancellationToken = default);
}

public sealed class CashPaymentWorkflowService(
    CashCheckoutService checkout,
    ILocalOrderRepository orderRepository,
    ISyncQueueRepository syncQueueRepository,
    IOrderUploadService? orderUploadService = null,
    ICardTerminalClient? cardTerminalClient = null,
    IVoucherTenderClient? voucherTenderClient = null) : ICashPaymentWorkflowService
{
    private readonly CashRoundingPolicy _cashRoundingPolicy = new();
    private readonly ICardTerminalClient _cardTerminalClient = cardTerminalClient ?? UnavailableCardTerminalClient.Instance;
    private readonly IVoucherTenderClient _voucherTenderClient = voucherTenderClient ?? UnavailableVoucherTenderClient.Instance;

    public bool TryParseTenderedAmount(string? amountTenderedText, out decimal tenderedAmount)
    {
        if (string.IsNullOrWhiteSpace(amountTenderedText))
        {
            tenderedAmount = 0m;
            return false;
        }

        return decimal.TryParse(amountTenderedText, NumberStyles.Number, CultureInfo.CurrentCulture, out tenderedAmount)
            || decimal.TryParse(amountTenderedText, NumberStyles.Number, CultureInfo.InvariantCulture, out tenderedAmount);
    }

    public decimal CalculateChange(string? amountTenderedText, decimal actualAmount)
    {
        if (RoundCurrency(actualAmount) < 0m)
        {
            return 0m;
        }

        if (!TryParseTenderedAmount(amountTenderedText, out var tenderedAmount))
        {
            return 0m;
        }

        var normalizedTenderedAmount = _cashRoundingPolicy.NormalizeCashTender(tenderedAmount);
        var roundedCashDue = _cashRoundingPolicy.CalculateRoundedCashDue(actualAmount);
        return _cashRoundingPolicy.CalculateChange(normalizedTenderedAmount, roundedCashDue);
    }

    public decimal CalculateTenderedAmount(IReadOnlyList<PaymentTender> tenders)
    {
        return RoundCurrency(tenders.Sum(tender => NormalizeTender(tender).Amount));
    }

    public decimal CalculateRemainingAmount(decimal actualAmount, IReadOnlyList<PaymentTender> tenders)
    {
        actualAmount = RoundCurrency(actualAmount);
        if (actualAmount < 0m)
        {
            return CalculateRefundRemainingAmount(actualAmount, tenders);
        }

        var normalizedTenders = tenders.Select(NormalizeTender).ToList();
        var nonCashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method != PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        var cashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        if (cashTotal <= 0m)
        {
            return RoundCurrency(actualAmount - nonCashTotal);
        }

        var roundedCashDue = _cashRoundingPolicy.CalculateRoundedCashDue(actualAmount, nonCashTotal);
        return RoundCurrency(roundedCashDue - cashTotal);
    }

    public decimal CalculateChange(IReadOnlyList<PaymentTender> tenders, decimal actualAmount)
    {
        if (RoundCurrency(actualAmount) < 0m)
        {
            return 0m;
        }

        var normalizedTenders = tenders.Select(NormalizeTender).ToList();
        var nonCashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method != PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        var cashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        if (cashTotal <= 0m)
        {
            return 0m;
        }

        var roundedCashDue = _cashRoundingPolicy.CalculateRoundedCashDue(actualAmount, nonCashTotal);
        return _cashRoundingPolicy.CalculateChange(cashTotal, roundedCashDue);
    }

    public async Task<PaymentTenderAttemptResult> AddTenderAsync(
        PaymentMethodKind method,
        PosSessionState session,
        decimal actualAmount,
        IReadOnlyList<PaymentTender> currentTenders,
        string? amountText,
        string? referenceText = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseTenderedAmount(amountText, out var amount) || amount <= 0m)
        {
            return PaymentTenderAttemptResult.Fail("payment.status.invalidAmount");
        }

        var isRefund = RoundCurrency(actualAmount) < 0m;
        var remainingAmount = CalculateRemainingAmount(actualAmount, currentTenders);
        if ((!isRefund && remainingAmount <= 0m) ||
            (isRefund && remainingAmount >= 0m))
        {
            return PaymentTenderAttemptResult.Fail("payment.status.alreadyFullyPaid");
        }

        if (!isRefund &&
            method == PaymentMethodKind.Voucher &&
            HasExistingVoucherTender(currentTenders, referenceText))
        {
            return PaymentTenderAttemptResult.Fail("payment.status.duplicateVoucher");
        }

        if (isRefund)
        {
            if (method == PaymentMethodKind.Card && string.IsNullOrWhiteSpace(referenceText))
            {
                return PaymentTenderAttemptResult.Fail("payment.status.cardDeclined", "Original card payment reference is required.");
            }

            return method switch
            {
                PaymentMethodKind.Cash => CreateRefundCashTenderAttempt(amount),
                PaymentMethodKind.Card => await AuthorizeRefundTenderAsync(
                    amount,
                    CalculateExternalRemainingAmount(actualAmount, currentTenders),
                    session,
                    referenceText,
                    cancellationToken,
                    _cardTerminalClient.RefundAsync,
                    PaymentMethodKind.Card,
                    "payment.status.cardExceedsRemaining",
                    "payment.status.cardDeclined",
                    "payment.status.cardTenderAdded"),
                PaymentMethodKind.Voucher => AuthorizeRefundTenderAsync(
                    amount,
                    CalculateExternalRemainingAmount(actualAmount, currentTenders),
                    session,
                    referenceText,
                    cancellationToken,
                    PaymentMethodKind.Voucher,
                    "payment.status.voucherExceedsRemaining",
                    "payment.status.voucherTenderAdded"),
                _ => PaymentTenderAttemptResult.Fail("payment.status.unsupportedMethod")
            };
        }

        return method switch
        {
            PaymentMethodKind.Cash => CreateCashTenderAttempt(amount),
            PaymentMethodKind.Card => await AuthorizeExternalTenderAsync(
                amount,
                CalculateExternalRemainingAmount(actualAmount, currentTenders),
                session,
                null,
                cancellationToken,
                (paymentAmount, paymentSession, _, token) => _cardTerminalClient.AuthorizeAsync(paymentAmount, paymentSession, token),
                PaymentMethodKind.Card,
                "payment.status.cardExceedsRemaining",
                "payment.status.cardDeclined",
                "payment.status.cardTenderAdded"),
            PaymentMethodKind.Voucher => await AuthorizeExternalTenderAsync(
                amount,
                CalculateExternalRemainingAmount(actualAmount, currentTenders),
                session,
                referenceText,
                cancellationToken,
                _voucherTenderClient.RedeemAsync,
                PaymentMethodKind.Voucher,
                "payment.status.voucherExceedsRemaining",
                "payment.status.voucherDeclined",
                "payment.status.voucherTenderAdded"),
            _ => PaymentTenderAttemptResult.Fail("payment.status.unsupportedMethod")
        };
    }

    public async Task<CashPaymentWorkflowResult> CompleteAsync(
        PosCartService cart,
        PosSessionState session,
        string? amountTenderedText,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseTenderedAmount(amountTenderedText, out var tenderedAmount))
        {
            throw new InvalidOperationException("Tendered amount is invalid.");
        }

        var result = checkout.CreateCashOrder(cart, session, tenderedAmount);
        await orderRepository.SavePendingOrderAsync(result.Order, cancellationToken);

        cart.Clear();

        var pendingSyncCount = await syncQueueRepository.CountPendingAsync(cancellationToken);
        var updatedSession = session with { PendingSyncCount = pendingSyncCount };

        return new CashPaymentWorkflowResult(
            result.Order,
            result.TenderedAmount,
            result.ChangeAmount,
            pendingSyncCount,
            updatedSession);
    }

    public async Task<CashPaymentWorkflowResult> CompletePaymentAsync(
        PosCartService cart,
        PosSessionState session,
        IReadOnlyList<PaymentTender> tenders,
        decimal cashTenderedAmount,
        CancellationToken cancellationToken = default)
    {
        var result = checkout.CreatePaymentOrder(cart, session, tenders, cashTenderedAmount);
        var order = await IssueRefundVouchersAsync(result.Order, session, cancellationToken);
        result = result with { Order = order };
        await orderRepository.SavePendingOrderAsync(order, cancellationToken);

        var hasPositiveVoucher = result.Order.Payments.Any(payment =>
            payment.Method == Hbpos.Contracts.Orders.PaymentMethodKind.Voucher &&
            payment.Amount > 0m);
        if (hasPositiveVoucher)
        {
            if (orderUploadService is null)
            {
                throw new InvalidOperationException("Voucher payments require online order upload.");
            }

            try
            {
                await orderUploadService.UploadOrderAsync(result.Order.OrderGuid, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new PaymentUploadFailedException(
                    result.Order.OrderGuid,
                    CalculateTenderedAmount(tenders),
                    result.ChangeAmount,
                    ex.Message,
                    ex);
            }
        }

        cart.Clear();

        var pendingSyncCount = await syncQueueRepository.CountPendingAsync(cancellationToken);
        var updatedSession = session with { PendingSyncCount = pendingSyncCount };

        return new CashPaymentWorkflowResult(
            result.Order,
            CalculateTenderedAmount(tenders),
            result.ChangeAmount,
            pendingSyncCount,
            updatedSession);
    }

    public async Task<CashPaymentWorkflowResult> RetryVoucherUploadAsync(
        Guid orderGuid,
        PosCartService cart,
        PosSessionState session,
        decimal tenderedAmount,
        decimal changeAmount,
        CancellationToken cancellationToken = default)
    {
        if (orderUploadService is null)
        {
            throw new InvalidOperationException("Voucher payments require online order upload.");
        }

        var order = await orderRepository.GetOrderAsync(orderGuid, cancellationToken)
            ?? throw new InvalidOperationException("Pending voucher order was not found.");

        try
        {
            await orderUploadService.UploadOrderAsync(orderGuid, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PaymentUploadFailedException(
                orderGuid,
                tenderedAmount,
                changeAmount,
                ex.Message,
                ex);
        }

        cart.Clear();

        var pendingSyncCount = await syncQueueRepository.CountPendingAsync(cancellationToken);
        var updatedSession = session with { PendingSyncCount = pendingSyncCount };

        return new CashPaymentWorkflowResult(
            order,
            tenderedAmount,
            changeAmount,
            pendingSyncCount,
            updatedSession);
    }

    private static async Task<PaymentTenderAttemptResult> AuthorizeExternalTenderAsync(
        decimal amount,
        decimal remainingAmount,
        PosSessionState session,
        string? referenceText,
        CancellationToken cancellationToken,
        Func<decimal, PosSessionState, string?, CancellationToken, Task<PaymentAuthorizationResult>> authorizeAsync,
        PaymentMethodKind method,
        string exceedsRemainingStatusKey,
        string declinedStatusKey,
        string approvedStatusKey)
    {
        if (amount > remainingAmount)
        {
            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        var authorization = await authorizeAsync(amount, session, referenceText, cancellationToken);
        if (!authorization.Approved)
        {
            return PaymentTenderAttemptResult.Fail(
                declinedStatusKey,
                authorization.Message);
        }

        var authorizedAmount = decimal.Round(
            authorization.AuthorizedAmount ?? amount,
            2,
            MidpointRounding.AwayFromZero);
        if (authorizedAmount <= 0m)
        {
            return PaymentTenderAttemptResult.Fail(declinedStatusKey, authorization.Message);
        }

        if (authorizedAmount > remainingAmount)
        {
            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        if (method == PaymentMethodKind.Card && authorizedAmount != amount)
        {
            return PaymentTenderAttemptResult.Fail(
                declinedStatusKey,
                "Card terminal authorized amount did not match the requested amount.");
        }

        return PaymentTenderAttemptResult.Success(
            new PaymentTender(method, authorizedAmount, authorization.Reference, CardTransactions: authorization.CardTransactions),
            approvedStatusKey);
    }

    private static async Task<PaymentTenderAttemptResult> AuthorizeRefundTenderAsync(
        decimal amount,
        decimal remainingAmount,
        PosSessionState session,
        string? referenceText,
        CancellationToken cancellationToken,
        Func<decimal, PosSessionState, string?, CancellationToken, Task<PaymentAuthorizationResult>> authorizeAsync,
        PaymentMethodKind method,
        string exceedsRemainingStatusKey,
        string declinedStatusKey,
        string approvedStatusKey)
    {
        if (amount > remainingAmount)
        {
            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        var authorization = await authorizeAsync(amount, session, referenceText, cancellationToken);
        if (!authorization.Approved)
        {
            return PaymentTenderAttemptResult.Fail(
                declinedStatusKey,
                authorization.Message);
        }

        var authorizedAmount = decimal.Round(
            authorization.AuthorizedAmount ?? amount,
            2,
            MidpointRounding.AwayFromZero);
        if (authorizedAmount <= 0m)
        {
            return PaymentTenderAttemptResult.Fail(declinedStatusKey, authorization.Message);
        }

        if (authorizedAmount > remainingAmount)
        {
            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        if (method == PaymentMethodKind.Card && authorizedAmount != amount)
        {
            return PaymentTenderAttemptResult.Fail(
                declinedStatusKey,
                "Card terminal authorized amount did not match the requested amount.");
        }

        var reference = method == PaymentMethodKind.Card
            ? CardRefundReference.Format(authorization.Reference, referenceText!)
            : authorization.Reference;
        return PaymentTenderAttemptResult.Success(
            new PaymentTender(method, -authorizedAmount, reference, CardTransactions: authorization.CardTransactions),
            approvedStatusKey);
    }

    private static PaymentTenderAttemptResult AuthorizeRefundTenderAsync(
        decimal amount,
        decimal remainingAmount,
        PosSessionState session,
        string? referenceText,
        CancellationToken cancellationToken,
        PaymentMethodKind method,
        string exceedsRemainingStatusKey,
        string approvedStatusKey)
    {
        _ = session;
        _ = referenceText;
        cancellationToken.ThrowIfCancellationRequested();

        if (amount > remainingAmount)
        {
            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        return PaymentTenderAttemptResult.Success(
            new PaymentTender(method, -RoundCurrency(amount), "VOUCHER_REFUND_PENDING", IdempotencyKey: Guid.NewGuid().ToString("N")),
            approvedStatusKey);
    }

    private async Task<LocalOrder> IssueRefundVouchersAsync(
        LocalOrder order,
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        var updatedPayments = new List<LocalPayment>(order.Payments.Count);
        var changed = false;

        foreach (var payment in order.Payments)
        {
            if (payment.Method != PaymentMethodKind.Voucher || payment.Amount >= 0m)
            {
                updatedPayments.Add(payment);
                continue;
            }

            var authorization = await _voucherTenderClient.IssueRefundAsync(
                Math.Abs(payment.Amount),
                session,
                order.OrderGuid.ToString("D"),
                string.IsNullOrWhiteSpace(payment.IdempotencyKey)
                    ? $"{order.OrderGuid:D}:{payment.PaymentGuid:D}"
                    : payment.IdempotencyKey.Trim(),
                "Refund",
                cancellationToken);
            if (!authorization.Approved || string.IsNullOrWhiteSpace(authorization.Reference))
            {
                throw new InvalidOperationException(authorization.Message ?? "Voucher refund issuing failed.");
            }

            updatedPayments.Add(payment with { Reference = authorization.Reference });
            changed = true;
        }

        return changed
            ? order with { Payments = updatedPayments }
            : order;
    }

    private static bool HasExistingVoucherTender(
        IReadOnlyList<PaymentTender> currentTenders,
        string? voucherCode)
    {
        var normalizedVoucherCode = NormalizeVoucherCode(voucherCode);
        if (string.IsNullOrWhiteSpace(normalizedVoucherCode))
        {
            return false;
        }

        return currentTenders
            .Where(tender => tender.Method == PaymentMethodKind.Voucher)
            .Select(tender => NormalizeVoucherCode(ParseVoucherCodeFromReference(tender.Reference)))
            .Any(existing => string.Equals(existing, normalizedVoucherCode, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ParseVoucherCodeFromReference(string? reference)
    {
        var parts = (reference ?? string.Empty).Split(':', StringSplitOptions.TrimEntries);
        return parts.Length >= 2 &&
            (parts[0].Equals("VOUCHER", StringComparison.OrdinalIgnoreCase) ||
             parts[0].Equals("VOUCHER_REFUND", StringComparison.OrdinalIgnoreCase))
                ? parts[1]
                : reference;
    }

    private static string? NormalizeVoucherCode(string? voucherCode)
    {
        return string.IsNullOrWhiteSpace(voucherCode) ? null : voucherCode.Trim();
    }

    private PaymentTenderAttemptResult CreateCashTenderAttempt(decimal amount)
    {
        var normalizedAmount = _cashRoundingPolicy.NormalizeCashTender(amount);
        return normalizedAmount <= 0m
            ? PaymentTenderAttemptResult.Fail("payment.status.invalidAmount")
            : PaymentTenderAttemptResult.Success(
                new PaymentTender(PaymentMethodKind.Cash, normalizedAmount),
                "payment.status.cashTenderAdded");
    }

    private PaymentTenderAttemptResult CreateRefundCashTenderAttempt(decimal amount)
    {
        var normalizedAmount = _cashRoundingPolicy.NormalizeCashTender(amount);
        return normalizedAmount <= 0m
            ? PaymentTenderAttemptResult.Fail("payment.status.invalidAmount")
            : PaymentTenderAttemptResult.Success(
                new PaymentTender(PaymentMethodKind.Cash, -normalizedAmount),
                "payment.status.cashTenderAdded");
    }

    private decimal CalculateExternalRemainingAmount(decimal actualAmount, IReadOnlyList<PaymentTender> currentTenders)
    {
        var remaining = RoundCurrency(RoundCurrency(actualAmount) - CalculateTenderedAmountForActualBalance(currentTenders));
        return Math.Abs(remaining);
    }

    private decimal CalculateTenderedAmountForActualBalance(IReadOnlyList<PaymentTender> tenders)
    {
        return RoundCurrency(tenders.Sum(tender => NormalizeTender(tender).Amount));
    }

    private PaymentTender NormalizeTender(PaymentTender tender)
    {
        var normalizedAmount = tender.Method == PaymentMethodKind.Cash
            ? NormalizeCashTender(tender.Amount)
            : RoundCurrency(tender.Amount);
        return tender with { Amount = normalizedAmount };
    }

    private decimal CalculateRefundRemainingAmount(decimal actualAmount, IReadOnlyList<PaymentTender> tenders)
    {
        var normalizedTenders = tenders.Select(NormalizeTender).ToList();
        var nonCashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method != PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        var cashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        if (cashTotal >= 0m)
        {
            return RoundCurrency(actualAmount - nonCashTotal);
        }

        var roundedCashRefund = _cashRoundingPolicy.CalculateRoundedCashDue(Math.Abs(actualAmount), Math.Abs(nonCashTotal));
        return RoundCurrency(cashTotal + roundedCashRefund);
    }

    private decimal NormalizeCashTender(decimal amount)
    {
        return amount < 0m
            ? -_cashRoundingPolicy.NormalizeCashTender(Math.Abs(amount))
            : _cashRoundingPolicy.NormalizeCashTender(amount);
    }

    private static decimal RoundCurrency(decimal amount)
    {
        return decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
    }
}

public interface ICardTerminalClient
{
    Task<PaymentAuthorizationResult> AuthorizeAsync(
        decimal amount,
        PosSessionState session,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        string? originalReference,
        CancellationToken cancellationToken = default);
}

public interface IVoucherTenderClient
{
    Task<PaymentAuthorizationResult> RedeemAsync(
        decimal amount,
        PosSessionState session,
        string? voucherCode,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> IssueRefundAsync(
        decimal amount,
        PosSessionState session,
        string orderReference,
        string idempotencyKey,
        string? reason = null,
        CancellationToken cancellationToken = default);
}

public sealed record PaymentAuthorizationResult(
    bool Approved,
    string? Reference = null,
    string? Message = null,
    decimal? AuthorizedAmount = null,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null);

public sealed record PaymentTenderAttemptResult(
    bool Succeeded,
    string StatusKey,
    PaymentTender? Tender = null,
    string? StatusMessage = null)
{
    public static PaymentTenderAttemptResult Success(PaymentTender tender, string statusKey)
    {
        return new PaymentTenderAttemptResult(true, statusKey, tender);
    }

    public static PaymentTenderAttemptResult Fail(string statusKey, string? statusMessage = null)
    {
        return new PaymentTenderAttemptResult(false, statusKey, null, statusMessage);
    }
}

public sealed class UnavailableCardTerminalClient : ICardTerminalClient
{
    public static UnavailableCardTerminalClient Instance { get; } = new();

    private UnavailableCardTerminalClient()
    {
    }

    public Task<PaymentAuthorizationResult> AuthorizeAsync(
        decimal amount,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentAuthorizationResult(false));
    }

    public Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentAuthorizationResult(false));
    }
}

public sealed class UnavailableVoucherTenderClient : IVoucherTenderClient
{
    public static UnavailableVoucherTenderClient Instance { get; } = new();

    private UnavailableVoucherTenderClient()
    {
    }

    public Task<PaymentAuthorizationResult> RedeemAsync(
        decimal amount,
        PosSessionState session,
        string? voucherCode,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentAuthorizationResult(false));
    }

    public Task<PaymentAuthorizationResult> IssueRefundAsync(
        decimal amount,
        PosSessionState session,
        string orderReference,
        string idempotencyKey,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentAuthorizationResult(false));
    }
}

public sealed record CashPaymentWorkflowResult(
    LocalOrder Order,
    decimal TenderedAmount,
    decimal ChangeAmount,
    int PendingSyncCount,
    PosSessionState UpdatedSession);

public sealed class PaymentUploadFailedException : InvalidOperationException
{
    public PaymentUploadFailedException(
        Guid orderGuid,
        decimal tenderedAmount,
        decimal changeAmount,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        OrderGuid = orderGuid;
        TenderedAmount = tenderedAmount;
        ChangeAmount = changeAmount;
    }

    public Guid OrderGuid { get; }

    public decimal TenderedAmount { get; }

    public decimal ChangeAmount { get; }
}
