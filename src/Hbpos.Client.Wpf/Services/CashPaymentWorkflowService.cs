using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hbpos.Client.Wpf.Localization;
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
        CancellationToken cancellationToken = default,
        PosCartSnapshot? cartSnapshot = null);

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
    IVoucherTenderClient? voucherTenderClient = null,
    ILocalCardPaymentAttemptRepository? cardPaymentAttemptRepository = null,
    ICardTerminalSettingsProvider? cardTerminalSettingsProvider = null,
    ILocalSquarePaymentAttemptRepository? squarePaymentAttemptRepository = null,
    ILinklyPaymentAttemptContextAccessor? linklyPaymentAttemptContextAccessor = null,
    ISquarePaymentAttemptContextAccessor? squarePaymentAttemptContextAccessor = null) : ICashPaymentWorkflowService
{
    private static readonly JsonSerializerOptions CardAttemptJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
        CancellationToken cancellationToken = default,
        PosCartSnapshot? cartSnapshot = null)
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
                ConsoleLog.Write("CardRefund", "workflow blocked card refund reason=missing-original-reference");
                return PaymentTenderAttemptResult.Fail("payment.status.cardDeclined", "Original card payment reference is required.");
            }

            return method switch
            {
                PaymentMethodKind.Cash => CreateRefundCashTenderAttempt(amount),
                PaymentMethodKind.Card => await AuthorizeCardTenderAsync(
                    amount,
                    CalculateExternalRemainingAmount(actualAmount, currentTenders),
                    session,
                    actualAmount,
                    currentTenders,
                    cartSnapshot,
                    referenceText,
                    cancellationToken,
                    isRefund: true,
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
            PaymentMethodKind.Card => await AuthorizeCardTenderAsync(
                amount,
                CalculateExternalRemainingAmount(actualAmount, currentTenders),
                session,
                actualAmount,
                currentTenders,
                cartSnapshot,
                null,
                cancellationToken,
                isRefund: false,
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
        // 退款代金券先以待发券状态落本地，确保崩溃后仍能沿用原始幂等键恢复。
        var order = await PrepareOrderForRecoverableCardPersistenceAsync(
            PrepareOrderForVoucherRefundPersistence(result.Order),
            tenders,
            cancellationToken);
        await orderRepository.SavePendingOrderAsync(order, cancellationToken);
        try
        {
            order = await IssuePendingRefundVouchersAsync(order, session, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PaymentUploadFailedException(
                order.OrderGuid,
                CalculateTenderedAmount(tenders),
                result.ChangeAmount,
                ex.Message,
                ex);
        }

        result = result with { Order = order };

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

        await MarkCompletedCardAttemptsAsync(tenders, cancellationToken);
        await MarkCompletedSquareAttemptsAsync(tenders, cancellationToken);
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
        var order = await orderRepository.GetOrderAsync(orderGuid, cancellationToken)
            ?? throw new InvalidOperationException("Pending voucher order was not found.");
        try
        {
            order = await IssuePendingRefundVouchersAsync(order, session, cancellationToken);
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

        var hasPositiveVoucher = order.Payments.Any(payment =>
            payment.Method == PaymentMethodKind.Voucher &&
            payment.Amount > 0m);
        if (hasPositiveVoucher)
        {
            if (orderUploadService is null)
            {
                throw new InvalidOperationException("Voucher payments require online order upload.");
            }

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

    private async Task<PaymentTenderAttemptResult> AuthorizeCardTenderAsync(
        decimal amount,
        decimal remainingAmount,
        PosSessionState session,
        decimal actualAmount,
        IReadOnlyList<PaymentTender> currentTenders,
        PosCartSnapshot? cartSnapshot,
        string? referenceText,
        CancellationToken cancellationToken,
        bool isRefund,
        string exceedsRemainingStatusKey,
        string declinedStatusKey,
        string approvedStatusKey)
    {
        if (amount > remainingAmount)
        {
            ConsoleLog.Write(
                "CardRefund",
                $"workflow blocked card {(isRefund ? "refund" : "payment")} reason=amount-exceeds-remaining amount={amount:0.00} remaining={remainingAmount:0.00} originalReference={LogValue(referenceText)}");
            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        var operation = isRefund ? "refund" : "payment";
        ConsoleLog.Write(
            "CardRefund",
            $"workflow terminal {operation} start amount={amount:0.00} remaining={remainingAmount:0.00} originalReference={LogValue(referenceText)}");

        var attempt = await TryCreateCardPaymentAttemptAsync(
            amount,
            session,
            actualAmount,
            currentTenders,
            cartSnapshot,
            referenceText,
            isRefund,
            cancellationToken);
        var squareAttempt = await TryCreateSquarePaymentAttemptAsync(
            amount,
            session,
            actualAmount,
            currentTenders,
            cartSnapshot,
            isRefund,
            cancellationToken);

        PaymentAuthorizationResult authorization;
        using var linklyAttemptScope = attempt is null || linklyPaymentAttemptContextAccessor is null
            ? null
            : linklyPaymentAttemptContextAccessor.Begin(new LinklyPaymentAttemptContext(
                attempt.AttemptGuid,
                (sessionId, txnRef, updatedAt, bindCancellationToken) =>
                    cardPaymentAttemptRepository!.UpdateSessionAsync(
                        attempt.AttemptGuid,
                        sessionId,
                        txnRef,
                        updatedAt,
                        bindCancellationToken)));
        using var squareAttemptScope = squareAttempt is null || squarePaymentAttemptContextAccessor is null
            ? null
            : squarePaymentAttemptContextAccessor.Begin(new SquarePaymentAttemptContext(squareAttempt.AttemptGuid, squareAttempt.IdempotencyKey));
        try
        {
            authorization = isRefund
                ? await _cardTerminalClient.RefundAsync(amount, session, referenceText, cancellationToken)
                : await _cardTerminalClient.AuthorizeAsync(amount, session, cancellationToken);
        }
        catch
        {
            if (attempt is not null)
            {
                await cardPaymentAttemptRepository!.UpdateOutcomeAsync(
                    attempt.AttemptGuid,
                    LocalCardPaymentAttemptStatus.Failed,
                    null,
                    "Card terminal request failed before a final response was received.",
                    null,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
            }

            if (squareAttempt is not null)
            {
                await squarePaymentAttemptRepository!.MarkFailedAsync(
                    squareAttempt.AttemptGuid,
                    LocalSquarePaymentAttemptStatus.Failed,
                    null,
                    null,
                    null,
                    "Card terminal request failed before a final response was received.",
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
            }

            throw;
        }

        if (squareAttempt is not null && !authorization.Approved)
        {
            await squarePaymentAttemptRepository!.MarkFailedAsync(
                squareAttempt.AttemptGuid,
                MapSquareAuthorizationFailureStatus(authorization.Message),
                null,
                authorization.ResponseText,
                authorization.ResponseCode,
                authorization.Message,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }

        ConsoleLog.Write(
            "CardRefund",
            $"workflow terminal {operation} completed approved={authorization.Approved} reference={LogValue(authorization.Reference)} " +
            $"message={LogValue(authorization.Message)} authorizedAmount={authorization.AuthorizedAmount?.ToString("0.00") ?? "<null>"} " +
            $"cardTxCount={authorization.CardTransactions?.Count ?? 0}");

        if (!authorization.Approved)
        {
            if (attempt is not null)
            {
                await UpdateCardPaymentAttemptAfterAuthorizationAsync(attempt.AttemptGuid, authorization, cancellationToken);
            }

            return PaymentTenderAttemptResult.Fail(
                string.IsNullOrWhiteSpace(authorization.StatusKey) ? declinedStatusKey : authorization.StatusKey,
                authorization.Message);
        }

        var authorizedAmount = decimal.Round(
            authorization.AuthorizedAmount ?? amount,
            2,
            MidpointRounding.AwayFromZero);
        if (authorizedAmount <= 0m)
        {
            if (attempt is not null)
            {
                await UpdateCardPaymentAttemptAfterAuthorizationAsync(
                    attempt.AttemptGuid,
                    authorization,
                    cancellationToken,
                    LocalCardPaymentAttemptStatus.RequiresReview,
                    "Card terminal approved a non-positive amount. Supervisor review is required.");
            }

            return PaymentTenderAttemptResult.Fail(declinedStatusKey, authorization.Message);
        }

        if (authorizedAmount > remainingAmount)
        {
            if (attempt is not null)
            {
                await UpdateCardPaymentAttemptAfterAuthorizationAsync(
                    attempt.AttemptGuid,
                    authorization,
                    cancellationToken,
                    LocalCardPaymentAttemptStatus.RequiresReview,
                    "Card terminal authorized amount exceeded the remaining amount.");
            }

            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        if (authorizedAmount != amount)
        {
            const string amountMismatchMessage = "Card terminal authorized amount did not match the requested amount.";
            if (attempt is not null)
            {
                await UpdateCardPaymentAttemptAfterAuthorizationAsync(
                    attempt.AttemptGuid,
                    authorization,
                    cancellationToken,
                    LocalCardPaymentAttemptStatus.RequiresReview,
                    amountMismatchMessage);
            }

            return PaymentTenderAttemptResult.Fail(
                declinedStatusKey,
                amountMismatchMessage);
        }

        if (attempt is not null)
        {
            await UpdateCardPaymentAttemptAfterAuthorizationAsync(attempt.AttemptGuid, authorization, cancellationToken);
        }

        var reference = isRefund
            ? CardRefundReference.Format(authorization.Reference, referenceText!)
            : authorization.Reference;
        var successStatusKey = authorization.FallbackSucceeded
            ? "payment.linklyFallback.succeeded"
            : approvedStatusKey;
        var successStatusMessage = authorization.FallbackSucceeded
            ? string.Format(
                CultureInfo.CurrentCulture,
                T("payment.linklyFallback.succeeded"),
                FormatLinklyModeDisplayName(authorization.RequestedConnectionMode),
                FormatLinklyModeDisplayName(authorization.ActualConnectionMode),
                T("payment.linklyFallback.promotePrimary"))
            : null;

        return PaymentTenderAttemptResult.Success(
            new PaymentTender(
                PaymentMethodKind.Card,
                isRefund ? -authorizedAmount : authorizedAmount,
                reference,
                CardTransactions: authorization.CardTransactions,
                IdempotencyKey: attempt is not null
                    ? FormatCardAttemptTenderKey(attempt.AttemptGuid)
                    : squareAttempt is not null
                        ? FormatSquareAttemptTenderKey(squareAttempt.AttemptGuid)
                        : null),
            successStatusKey,
            successStatusMessage);
    }

    private async Task<LocalCardPaymentAttempt?> TryCreateCardPaymentAttemptAsync(
        decimal amount,
        PosSessionState session,
        decimal actualAmount,
        IReadOnlyList<PaymentTender> currentTenders,
        PosCartSnapshot? cartSnapshot,
        string? referenceText,
        bool isRefund,
        CancellationToken cancellationToken)
    {
        if (cardPaymentAttemptRepository is null || cardTerminalSettingsProvider is null || cartSnapshot is null)
        {
            return null;
        }

        var settings = await cardTerminalSettingsProvider.GetSettingsAsync(cancellationToken);
        if (settings.Processor != CardProcessorKind.Linkly ||
            CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode) != LinklyConnectionMode.CloudBackendAsync)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var draft = new CardPaymentOrderDraft(
            Guid.NewGuid(),
            session,
            cartSnapshot,
            currentTenders.ToArray(),
            actualAmount,
            amount,
            isRefund ? "R" : "P",
            referenceText,
            now);
        var attempt = new LocalCardPaymentAttempt(
            Guid.NewGuid(),
            null,
            null,
            settings.Processor.ToString(),
            settings.Environment.ToString(),
            CardTerminalSettings.FormatLinklyConnectionMode(settings.LinklyConnectionMode),
            isRefund ? "R" : "P",
            amount,
            LocalCardPaymentAttemptStatus.Pending,
            JsonSerializer.Serialize(draft, CardAttemptJsonOptions),
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            null,
            null,
            null,
            now,
            now,
            null,
            null);

        await cardPaymentAttemptRepository.CreateAsync(attempt, cancellationToken);
        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "payment-attempt",
            "created",
            environment: settings.Environment,
            details: new
            {
                timestamp = DateTimeOffset.Now,
                attemptGuid = attempt.AttemptGuid,
                localStatus = attempt.Status.ToString(),
                txnType = attempt.TxnType,
                amount = attempt.Amount,
                processor = attempt.Processor,
                connectionMode = attempt.ConnectionMode,
                storeCode = attempt.StoreCode,
                deviceCode = attempt.DeviceCode,
                cashierId = attempt.CashierId,
                createdAt = attempt.CreatedAt,
                updatedAt = attempt.UpdatedAt
            });
        return attempt;
    }

    private async Task<LocalSquarePaymentAttempt?> TryCreateSquarePaymentAttemptAsync(
        decimal amount,
        PosSessionState session,
        decimal actualAmount,
        IReadOnlyList<PaymentTender> currentTenders,
        PosCartSnapshot? cartSnapshot,
        bool isRefund,
        CancellationToken cancellationToken)
    {
        if (squarePaymentAttemptRepository is null ||
            cardTerminalSettingsProvider is null ||
            cartSnapshot is null ||
            isRefund)
        {
            return null;
        }

        var settings = await cardTerminalSettingsProvider.GetSettingsAsync(cancellationToken);
        if (settings.Processor != CardProcessorKind.Square ||
            string.IsNullOrWhiteSpace(settings.SquareDeviceId) ||
            string.IsNullOrWhiteSpace(settings.SquareLocationId))
        {
            return null;
        }

        const string currency = "AUD";
        var now = DateTimeOffset.UtcNow;
        var draft = new CardPaymentOrderDraft(
            Guid.NewGuid(),
            session,
            cartSnapshot,
            currentTenders.ToArray(),
            actualAmount,
            amount,
            "P",
            null,
            now);
        var attempt = new LocalSquarePaymentAttempt(
            Guid.NewGuid(),
            null,
            Guid.NewGuid().ToString("N"),
            SquareDeviceIdNormalizer.NormalizeForTerminalCheckout(settings.SquareDeviceId) ?? settings.SquareDeviceId,
            settings.SquareLocationId,
            settings.Environment.ToString(),
            amount,
            ToMinorUnits(amount),
            currency,
            LocalSquarePaymentAttemptStatus.Pending,
            null,
            null,
            JsonSerializer.Serialize(draft, CardAttemptJsonOptions),
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            null,
            null,
            null,
            null,
            now,
            now,
            null,
            null,
            null);

        await squarePaymentAttemptRepository.CreateAsync(attempt, cancellationToken);
        return attempt;
    }

    private async Task UpdateCardPaymentAttemptAfterAuthorizationAsync(
        Guid attemptGuid,
        PaymentAuthorizationResult authorization,
        CancellationToken cancellationToken,
        LocalCardPaymentAttemptStatus? statusOverride = null,
        string? responseTextOverride = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(authorization.SessionId))
        {
            await cardPaymentAttemptRepository!.UpdateSessionAsync(
                attemptGuid,
                authorization.SessionId,
                authorization.TxnRef,
                now,
                cancellationToken);
        }

        var firstTransaction = authorization.CardTransactions?.FirstOrDefault();
        var status = statusOverride ?? (authorization.Approved
            ? LocalCardPaymentAttemptStatus.Approved
            : MapCardAttemptFailureStatus(authorization.Message, firstTransaction?.ResponseText));
        await cardPaymentAttemptRepository!.UpdateOutcomeAsync(
            attemptGuid,
            status,
            firstTransaction?.ResponseCode ?? authorization.ResponseCode,
            responseTextOverride ?? firstTransaction?.ResponseText ?? authorization.ResponseText ?? authorization.Message,
            authorization.Reference,
            now,
            cancellationToken);
    }

    private static LocalCardPaymentAttemptStatus MapCardAttemptFailureStatus(
        string? message,
        string? responseText)
    {
        var text = $"{message} {responseText}".ToUpperInvariant();
        if (text.Contains("TIMEOUT", StringComparison.Ordinal))
        {
            return LocalCardPaymentAttemptStatus.TimedOut;
        }

        if (text.Contains("CANCEL", StringComparison.Ordinal))
        {
            return LocalCardPaymentAttemptStatus.Cancelled;
        }

        if (text.Contains("DECLIN", StringComparison.Ordinal))
        {
            return LocalCardPaymentAttemptStatus.Declined;
        }

        return LocalCardPaymentAttemptStatus.Failed;
    }

    private static LocalSquarePaymentAttemptStatus MapSquareAuthorizationFailureStatus(string? message)
    {
        var text = (message ?? string.Empty).ToUpperInvariant();
        if (text.Contains("TIMEOUT", StringComparison.Ordinal))
        {
            return LocalSquarePaymentAttemptStatus.TimedOut;
        }

        if (text.Contains("CANCEL", StringComparison.Ordinal))
        {
            return LocalSquarePaymentAttemptStatus.Canceled;
        }

        if (text.Contains("UNKNOWN", StringComparison.Ordinal) ||
            text.Contains("CONFIRM", StringComparison.Ordinal))
        {
            return LocalSquarePaymentAttemptStatus.Unknown;
        }

        return LocalSquarePaymentAttemptStatus.Failed;
    }

    private static long ToMinorUnits(decimal amount)
    {
        return decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
    }

    private static string FormatCardAttemptTenderKey(Guid attemptGuid)
    {
        return $"CARD_ATTEMPT:{attemptGuid:N}";
    }

    private static string FormatSquareAttemptTenderKey(Guid attemptGuid)
    {
        return $"SQUARE_ATTEMPT:{attemptGuid:N}";
    }

    private static bool TryReadCardAttemptTenderKey(string? value, out Guid attemptGuid)
    {
        attemptGuid = Guid.Empty;
        const string prefix = "CARD_ATTEMPT:";
        return !string.IsNullOrWhiteSpace(value) &&
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(value[prefix.Length..], "N", out attemptGuid);
    }

    private static bool TryReadSquareAttemptTenderKey(string? value, out Guid attemptGuid)
    {
        attemptGuid = Guid.Empty;
        const string prefix = "SQUARE_ATTEMPT:";
        return !string.IsNullOrWhiteSpace(value) &&
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(value[prefix.Length..], "N", out attemptGuid);
    }

    private async Task MarkCompletedCardAttemptsAsync(
        IReadOnlyList<PaymentTender> tenders,
        CancellationToken cancellationToken)
    {
        if (cardPaymentAttemptRepository is null)
        {
            return;
        }

        foreach (var attemptGuid in tenders
            .Select(tender => TryReadCardAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid) ? attemptGuid : (Guid?)null)
            .Where(attemptGuid => attemptGuid is not null)
            .Select(attemptGuid => attemptGuid!.Value)
            .Distinct())
        {
            // 订单落本地后才把刷卡 attempt 标为完成，避免“刷卡成功但订单未写入”被误判为已恢复。
            await cardPaymentAttemptRepository.MarkOrderCompletedAsync(
                attemptGuid,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
    }

    private async Task MarkCompletedSquareAttemptsAsync(
        IReadOnlyList<PaymentTender> tenders,
        CancellationToken cancellationToken)
    {
        if (squarePaymentAttemptRepository is null)
        {
            return;
        }

        foreach (var attemptGuid in tenders
            .Select(tender => TryReadSquareAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid) ? attemptGuid : (Guid?)null)
            .Where(attemptGuid => attemptGuid is not null)
            .Select(attemptGuid => attemptGuid!.Value)
            .Distinct())
        {
            // 只有订单真正保存后，Square attempt 才能标为完成，避免恢复时漏救订单。
            await squarePaymentAttemptRepository.MarkOrderCompletedAsync(
                attemptGuid,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
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
            if (method == PaymentMethodKind.Card)
            {
                ConsoleLog.Write(
                    "CardRefund",
                    $"workflow blocked card refund reason=amount-exceeds-remaining amount={amount:0.00} remaining={remainingAmount:0.00} originalReference={LogValue(referenceText)}");
            }

            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        if (method == PaymentMethodKind.Card)
        {
            var operation = string.IsNullOrWhiteSpace(referenceText) ? "payment" : "refund";
            ConsoleLog.Write(
                "CardRefund",
                $"workflow terminal {operation} start amount={amount:0.00} remaining={remainingAmount:0.00} originalReference={LogValue(referenceText)}");
        }

        var authorization = await authorizeAsync(amount, session, referenceText, cancellationToken);
        if (method == PaymentMethodKind.Card)
        {
            var operation = string.IsNullOrWhiteSpace(referenceText) ? "payment" : "refund";
            ConsoleLog.Write(
                "CardRefund",
                $"workflow terminal {operation} completed approved={authorization.Approved} reference={LogValue(authorization.Reference)} " +
                $"message={LogValue(authorization.Message)} authorizedAmount={authorization.AuthorizedAmount?.ToString("0.00") ?? "<null>"} " +
                $"cardTxCount={authorization.CardTransactions?.Count ?? 0}");
        }

        if (!authorization.Approved)
        {
            return PaymentTenderAttemptResult.Fail(
                string.IsNullOrWhiteSpace(authorization.StatusKey) ? declinedStatusKey : authorization.StatusKey,
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

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
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

    private static LocalOrder PrepareOrderForVoucherRefundPersistence(LocalOrder order)
    {
        var updatedPayments = new List<LocalPayment>(order.Payments.Count);
        var changed = false;

        foreach (var payment in order.Payments)
        {
            if (!IsVoucherRefundPayment(payment))
            {
                updatedPayments.Add(payment);
                continue;
            }

            var normalizedIdempotencyKey = EnsureRefundVoucherIdempotencyKey(order.OrderGuid, payment);
            var normalizedReference = HasIssuedVoucherRefundReference(payment.Reference)
                ? payment.Reference?.Trim()
                : "VOUCHER_REFUND_PENDING";
            var updatedPayment = payment with
            {
                Reference = normalizedReference,
                IdempotencyKey = normalizedIdempotencyKey
            };
            updatedPayments.Add(updatedPayment);
            changed |= !Equals(updatedPayment, payment);
        }

        return changed
            ? order with { Payments = updatedPayments }
            : order;
    }

    private async Task<LocalOrder> PrepareOrderForRecoverableCardPersistenceAsync(
        LocalOrder order,
        IReadOnlyList<PaymentTender> tenders,
        CancellationToken cancellationToken)
    {
        var attemptGuid = tenders
            .Select(tender => TryReadCardAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid) ? attemptGuid : (Guid?)null)
            .FirstOrDefault(value => value is not null);
        if (attemptGuid is not null && cardPaymentAttemptRepository is not null)
        {
            var attempt = await cardPaymentAttemptRepository.GetAttemptAsync(attemptGuid.Value, cancellationToken);
            if (attempt is not null)
            {
                var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(attempt.OrderDraftJson, CardAttemptJsonOptions);
                // 正常落单和重启恢复必须使用同一个订单 GUID，避免崩溃后恢复重复保存订单。
                return draft is null ? order : order with { OrderGuid = draft.OrderGuid };
            }
        }

        var squareAttemptGuid = tenders
            .Select(tender => TryReadSquareAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid) ? attemptGuid : (Guid?)null)
            .FirstOrDefault(value => value is not null);
        if (squareAttemptGuid is not null && squarePaymentAttemptRepository is not null)
        {
            var attempt = await squarePaymentAttemptRepository.GetAttemptAsync(squareAttemptGuid.Value, cancellationToken);
            if (attempt is not null)
            {
                var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(attempt.OrderDraftJson, CardAttemptJsonOptions);
                // Square 使用独立 attempt 表，但订单 GUID 仍必须和刷卡前草稿保持一致。
                return draft is null ? order : order with { OrderGuid = draft.OrderGuid };
            }
        }

        return order;
    }

    private async Task<LocalOrder> IssuePendingRefundVouchersAsync(
        LocalOrder order,
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        var updatedPayments = new List<LocalPayment>(order.Payments.Count);
        var changed = false;

        foreach (var payment in order.Payments)
        {
            if (!IsPendingVoucherRefundPayment(payment))
            {
                updatedPayments.Add(payment);
                continue;
            }

            var idempotencyKey = EnsureRefundVoucherIdempotencyKey(order.OrderGuid, payment);
            var authorization = await _voucherTenderClient.IssueRefundAsync(
                Math.Abs(payment.Amount),
                session,
                order.OrderGuid.ToString("D"),
                idempotencyKey,
                "Refund",
                cancellationToken);
            if (!authorization.Approved || string.IsNullOrWhiteSpace(authorization.Reference))
            {
                throw new InvalidOperationException(authorization.Message ?? "Voucher refund issuing failed.");
            }

            // 每张退款券发券成功后立刻回写本地引用，避免后续步骤失败时再次展示为待处理。
            await orderRepository.UpdatePaymentReferenceAsync(payment.PaymentGuid, authorization.Reference, cancellationToken);
            updatedPayments.Add(payment with
            {
                Reference = authorization.Reference,
                IdempotencyKey = idempotencyKey
            });
            changed = true;
        }

        return changed
            ? order with { Payments = updatedPayments }
            : order;
    }

    private static bool IsVoucherRefundPayment(LocalPayment payment)
    {
        return payment.Method == PaymentMethodKind.Voucher && payment.Amount < 0m;
    }

    private static bool IsPendingVoucherRefundPayment(LocalPayment payment)
    {
        return IsVoucherRefundPayment(payment) && !HasIssuedVoucherRefundReference(payment.Reference);
    }

    private static bool HasIssuedVoucherRefundReference(string? reference)
    {
        return !string.IsNullOrWhiteSpace(reference) &&
            !string.Equals(reference.Trim(), "VOUCHER_REFUND_PENDING", StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureRefundVoucherIdempotencyKey(Guid orderGuid, LocalPayment payment)
    {
        return string.IsNullOrWhiteSpace(payment.IdempotencyKey)
            ? $"{orderGuid:D}:{payment.PaymentGuid:D}"
            : payment.IdempotencyKey.Trim();
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

    private static string T(string key)
    {
        return LocalizationResourceProvider.Instance[key];
    }

    private static string FormatLinklyModeDisplayName(string? modeText)
    {
        var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(modeText, LinklyConnectionMode.LocalIp);
        var key = mode switch
        {
            LinklyConnectionMode.CloudDirectSync => "settings.linkly.mode.cloudDirectSync",
            LinklyConnectionMode.CloudBackendAsync => "settings.linkly.mode.cloudBackendAsync",
            _ => "settings.linkly.mode.localIp"
        };

        // 支付页提示面向收银员，不能暴露 CloudBackendAsync 这类内部配置值。
        return T(key);
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
    IReadOnlyList<CardTransactionDto>? CardTransactions = null,
    string? Processor = null,
    string? Environment = null,
    string? ConnectionMode = null,
    string? TxnType = null,
    string? SessionId = null,
    string? TxnRef = null,
    string? ResponseCode = null,
    string? ResponseText = null,
    string? StatusKey = null,
    string? RequestedConnectionMode = null,
    string? ActualConnectionMode = null,
    IReadOnlyList<string>? FallbackAttemptedModes = null,
    bool FallbackSucceeded = false,
    bool FallbackAllowed = false,
    bool ResultUnknown = false);

public sealed record CardPaymentOrderDraft(
    Guid OrderGuid,
    PosSessionState Session,
    PosCartSnapshot CartSnapshot,
    IReadOnlyList<PaymentTender> CurrentTenders,
    decimal ActualAmount,
    decimal CardAmount,
    string TxnType,
    string? OriginalReference,
    DateTimeOffset CreatedAt);

public sealed record PaymentTenderAttemptResult(
    bool Succeeded,
    string StatusKey,
    PaymentTender? Tender = null,
    string? StatusMessage = null)
{
    public static PaymentTenderAttemptResult Success(PaymentTender tender, string statusKey, string? statusMessage = null)
    {
        return new PaymentTenderAttemptResult(true, statusKey, tender, statusMessage);
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
