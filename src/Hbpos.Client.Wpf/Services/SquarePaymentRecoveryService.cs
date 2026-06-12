using System.Text.Json;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface ISquarePaymentRecoveryService
{
    Task<CardPaymentRecoveryResult> RecoverLatestAsync(
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default);
}

public sealed class SquarePaymentRecoveryService(
    ILocalSquarePaymentAttemptRepository attemptRepository,
    ICardTerminalSettingsProvider settingsProvider,
    ISquareTerminalPaymentClient squareTerminalPaymentClient,
    CashCheckoutService checkout,
    ILocalOrderRepository orderRepository,
    ILocalizationService? localization = null) : ISquarePaymentRecoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CardPaymentRecoveryResult> RecoverLatestAsync(
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        if (settings.Processor != CardProcessorKind.Square)
        {
            return CardPaymentRecoveryResult.None;
        }

        var attempt = await attemptRepository.GetLatestOpenAttemptAsync(
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            settings.Environment.ToString(),
            cancellationToken);
        if (attempt is null)
        {
            return CardPaymentRecoveryResult.None;
        }

        var draft = DeserializeDraft(attempt);
        await attemptRepository.MarkRecoveringAsync(attempt.AttemptGuid, DateTimeOffset.UtcNow, cancellationToken);
        var checkingMessage = Format("cardRecovery.square.checking", "A previous Square card transaction for {0:C2} was in progress before the POS closed. Checking the card terminal status.", attempt.Amount);

        if (string.IsNullOrWhiteSpace(attempt.CheckoutId))
        {
            if (TryDeferForCurrentCart(cart, attempt, "missing-checkout-id", out var deferredResult))
            {
                return deferredResult;
            }

            cart.RestoreSnapshot(draft.CartSnapshot);
            await attemptRepository.MarkFailedAsync(
                attempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Unknown,
                attempt.CheckoutStatus,
                attempt.PaymentStatus,
                null,
                "Square checkout id was not recorded.",
                DateTimeOffset.UtcNow,
                cancellationToken);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.DraftRestored,
                T("cardRecovery.square.missingCheckoutId", "The previous Square card request may not have been submitted. Please take payment again."));
        }

        if (attempt.Status == LocalSquarePaymentAttemptStatus.PaymentVerified &&
            !string.IsNullOrWhiteSpace(attempt.PaymentId) &&
            !string.IsNullOrWhiteSpace(attempt.PaymentStatus))
        {
            if (TryDeferForCurrentCart(cart, attempt, "payment-already-verified", out var deferredResult))
            {
                return deferredResult;
            }

            return await CompleteVerifiedAttemptAsync(cart, attempt, draft, attempt.PaymentId!, attempt.PaymentStatus!, cancellationToken);
        }

        SquareCheckoutStatusResult checkoutStatus;
        try
        {
            checkoutStatus = await squareTerminalPaymentClient.GetCheckoutAsync(settings, attempt.CheckoutId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.Write("SquareRecovery", $"checkout lookup failed attemptGuid={attempt.AttemptGuid} checkoutId={attempt.CheckoutId} error={ex.GetType().Name}");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        if (IsSquarePendingStatus(checkoutStatus.Status))
        {
            await attemptRepository.UpdateCheckoutStatusAsync(
                attempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Recovering,
                checkoutStatus.Status,
                checkoutStatus.CancelReason,
                DateTimeOffset.UtcNow,
                cancellationToken);
            return new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Checking, checkingMessage);
        }

        if (TryDeferForCurrentCart(cart, attempt, $"checkout-final-{checkoutStatus.Status}", out var finalDeferredResult))
        {
            return finalDeferredResult;
        }

        if (string.Equals(checkoutStatus.Status, "CANCELED", StringComparison.OrdinalIgnoreCase))
        {
            cart.RestoreSnapshot(draft.CartSnapshot);
            await attemptRepository.UpdateCheckoutStatusAsync(
                attempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Canceled,
                checkoutStatus.Status,
                checkoutStatus.CancelReason,
                DateTimeOffset.UtcNow,
                cancellationToken);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.DraftRestored,
                Format("cardRecovery.square.cancelled", "The previous Square card payment was not completed: {0}. The order has been restored. Select a payment method again.", checkoutStatus.CancelReason ?? "CANCELED"));
        }

        if (!string.Equals(checkoutStatus.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            await attemptRepository.MarkFailedAsync(
                attempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Unknown,
                checkoutStatus.Status,
                attempt.PaymentStatus,
                null,
                $"Unexpected checkout status {checkoutStatus.Status}.",
                DateTimeOffset.UtcNow,
                cancellationToken);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        var paymentId = checkoutStatus.PaymentIds.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(paymentId))
        {
            await attemptRepository.MarkFailedAsync(
                attempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Unknown,
                checkoutStatus.Status,
                attempt.PaymentStatus,
                null,
                "Square checkout did not return a payment id.",
                DateTimeOffset.UtcNow,
                cancellationToken);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        SquarePaymentStatusResult payment;
        try
        {
            payment = await squareTerminalPaymentClient.GetPaymentAsync(settings, paymentId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.Write("SquareRecovery", $"payment lookup failed attemptGuid={attempt.AttemptGuid} checkoutId={attempt.CheckoutId} paymentId={paymentId} error={ex.GetType().Name}");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        var verification = SquarePaymentVerifier.Verify(
            payment.Status,
            payment.AmountCents,
            payment.Currency,
            attempt.AmountCents,
            attempt.Currency);
        if (!verification.Verified)
        {
            await attemptRepository.MarkFailedAsync(
                attempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Unknown,
                checkoutStatus.Status,
                payment.Status,
                null,
                verification.Message,
                DateTimeOffset.UtcNow,
                cancellationToken);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                verification.Failure == SquarePaymentVerificationFailure.Amount
                    ? T("cardRecovery.square.amountMismatch", "The payment amount returned by Square does not match the order amount. The order was not saved automatically. Ask a supervisor to confirm.")
                    : UnknownResultMessage());
        }

        await attemptRepository.MarkPaymentVerifiedAsync(
            attempt.AttemptGuid,
            payment.PaymentId,
            payment.Status,
            null,
            "Payment verified during recovery.",
            DateTimeOffset.UtcNow,
            cancellationToken);
        return await CompleteVerifiedAttemptAsync(cart, attempt, draft, payment.PaymentId, payment.Status, cancellationToken);
    }

    private async Task<CardPaymentRecoveryResult> CompleteVerifiedAttemptAsync(
        PosCartService cart,
        LocalSquarePaymentAttempt attempt,
        CardPaymentOrderDraft draft,
        string paymentId,
        string paymentStatus,
        CancellationToken cancellationToken)
    {
        cart.RestoreSnapshot(draft.CartSnapshot);
        var cardTender = new PaymentTender(
            PaymentMethodKind.Card,
            Math.Abs(draft.CardAmount),
            $"SQ:{paymentId}",
            CardTransactions:
            [
                new CardTransactionDto(
                    "Square",
                    paymentId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    paymentStatus,
                    null,
                    DateTimeOffset.UtcNow,
                    Math.Abs(draft.CardAmount),
                    null)
            ],
            IdempotencyKey: $"SQUARE_ATTEMPT:{attempt.AttemptGuid:N}");
        var tenders = draft.CurrentTenders.Concat([cardTender]).ToList();
        var cashTenderedAmount = tenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount);
        var checkoutResult = checkout.CreatePaymentOrder(cart, draft.Session, tenders, cashTenderedAmount);
        var order = checkoutResult.Order with { OrderGuid = draft.OrderGuid };
        var existingOrder = await orderRepository.GetOrderAsync(draft.OrderGuid, cancellationToken);
        if (existingOrder is null)
        {
            await orderRepository.SavePendingOrderAsync(order, cancellationToken);
        }
        else
        {
            order = existingOrder;
        }

        await attemptRepository.MarkOrderCompletedAsync(attempt.AttemptGuid, DateTimeOffset.UtcNow, cancellationToken);
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.OrderCompleted,
            T("cardRecovery.square.approved", "The previous Square card payment was successful. The order has been recovered and saved automatically."),
            order);
    }

    private bool TryDeferForCurrentCart(
        PosCartService cart,
        LocalSquarePaymentAttempt attempt,
        string reason,
        out CardPaymentRecoveryResult result)
    {
        if (cart.IsEmpty)
        {
            result = CardPaymentRecoveryResult.None;
            return false;
        }

        // 褰撳墠璐墿杞﹀凡鏈夋柊璁㈠崟鏃讹紝涓嶆仮澶嶆棫鑽夌銆佷笉淇濆瓨璁㈠崟锛屼篃涓嶆妸鏃?attempt 鏍囪涓哄凡澶勭悊銆?
        ConsoleLog.Write(
            "SquareRecovery",
            $"defer recovery because current cart is not empty attemptGuid={attempt.AttemptGuid} checkoutId={attempt.CheckoutId ?? "<null>"} reason={reason}");
        result = new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Unknown, CurrentCartNotEmptyMessage());
        return true;
    }

    private static CardPaymentOrderDraft DeserializeDraft(LocalSquarePaymentAttempt attempt)
    {
        return JsonSerializer.Deserialize<CardPaymentOrderDraft>(attempt.OrderDraftJson, JsonOptions)
            ?? throw new InvalidOperationException("Square payment order draft is invalid.");
    }

    private static bool IsSquarePendingStatus(string status)
    {
        return string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "CANCEL_REQUESTED", StringComparison.OrdinalIgnoreCase);
    }

    private string CurrentCartNotEmptyMessage()
    {
        return T("cardRecovery.square.currentCartNotEmpty", "The previous Square card result needs handling, but the current cart already contains items. Complete or clear the current cart before recovering the previous order.");
    }

    private string UnknownResultMessage()
    {
        return T("cardRecovery.square.unknown", "The previous Square card result cannot be confirmed. Ask a supervisor to confirm the Square backend status before continuing.");
    }

    private string T(string key, string fallback)
    {
        var value = localization?.T(key);
        return string.IsNullOrWhiteSpace(value) || value == $"[[{key}]]" ? fallback : value;
    }

    private string Format(string key, string fallback, params object[] args)
    {
        var template = T(key, fallback);
        return string.Format(localization?.CurrentCulture ?? System.Globalization.CultureInfo.CurrentCulture, template, args);
    }
}
