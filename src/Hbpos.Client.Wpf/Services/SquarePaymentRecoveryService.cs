using System.Text.Json;
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
    ILocalOrderRepository orderRepository) : ISquarePaymentRecoveryService
{
    private const string CurrentCartNotEmptyMessage = "检测到上一笔 Square 刷卡结果需要处理，但当前购物车已有商品。请先完成或清空当前购物车后再恢复上一笔订单。";
    private const string UnknownResultMessage = "无法确认上一笔 Square 刷卡结果。请联系主管确认 Square 后台状态后再继续。";
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
        var checkingMessage = $"检测到上次程序关闭前有一笔 {attempt.Amount:C2} Square 刷卡交易正在处理，正在查询刷卡机状态。";

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
                "上次 Square 刷卡请求可能未成功发出，请重新付款。");
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
                UnknownResultMessage);
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
                $"上一笔 Square 刷卡未完成：{checkoutStatus.CancelReason ?? "CANCELED"}。订单已恢复，请重新选择付款方式。");
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
                UnknownResultMessage);
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
                UnknownResultMessage);
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
                UnknownResultMessage);
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
                    ? "Square 返回的付款金额与订单金额不一致。订单未自动保存，请联系主管确认。"
                    : UnknownResultMessage);
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
            "上一笔 Square 刷卡已成功，订单已自动恢复并保存。",
            order);
    }

    private static bool TryDeferForCurrentCart(
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

        // 当前购物车已有新订单时，不恢复旧草稿、不保存订单，也不把旧 attempt 标记为已处理。
        ConsoleLog.Write(
            "SquareRecovery",
            $"defer recovery because current cart is not empty attemptGuid={attempt.AttemptGuid} checkoutId={attempt.CheckoutId ?? "<null>"} reason={reason}");
        result = new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Unknown, CurrentCartNotEmptyMessage);
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
}
