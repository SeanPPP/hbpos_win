using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public enum CardPaymentRecoveryOutcome
{
    None,
    Checking,
    OrderCompleted,
    DraftRestored,
    Unknown
}

public sealed record CardPaymentRecoveryResult(
    CardPaymentRecoveryOutcome Outcome,
    string Message,
    LocalOrder? Order = null,
    decimal TenderedAmount = 0m,
    decimal ChangeAmount = 0m,
    PosSessionState? UpdatedSession = null)
{
    public static CardPaymentRecoveryResult None { get; } = new(CardPaymentRecoveryOutcome.None, string.Empty);
}

public interface ICardPaymentRecoveryService
{
    Task<CardPaymentRecoveryResult> RecoverLatestAsync(
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default);
}

public sealed class CardPaymentRecoveryService(
    ILocalCardPaymentAttemptRepository attemptRepository,
    ICardTerminalSettingsProvider settingsProvider,
    ILinklyBackendTerminalClient backendTerminalClient,
    CashCheckoutService checkout,
    ILocalOrderRepository orderRepository,
    ISyncQueueRepository syncQueueRepository) : ICardPaymentRecoveryService
{
    private const string StatusCompleted = "Completed";
    private const string StatusFailed = "Failed";
    private const string StatusNotSubmitted = "NotSubmitted";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CardPaymentRecoveryResult> RecoverLatestAsync(
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        if (settings.Processor != CardProcessorKind.Linkly ||
            CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode) != LinklyConnectionMode.CloudBackendAsync)
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
        var checkingMessage = $"检测到上次程序关闭前有一笔 {attempt.Amount:C2} 刷卡交易正在处理，正在查询刷卡机状态。";
        await attemptRepository.MarkRecoveringAsync(attempt.AttemptGuid, DateTimeOffset.UtcNow, cancellationToken);

        LinklyCloudBackendSessionResponse? status = null;
        try
        {
            status = !string.IsNullOrWhiteSpace(attempt.SessionId)
                ? await backendTerminalClient.GetSessionStatusAsync(settings, attempt.SessionId, cancellationToken)
                : await backendTerminalClient.GetResumableSessionAsync(settings, cancellationToken);

            if (status is not null && !IsFinal(status))
            {
                status = await backendTerminalClient.RecoverSessionAsync(settings, status.SessionId, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.Write(
                "CardRecovery",
                $"recover status failed attemptGuid={attempt.AttemptGuid} sessionId={LogValue(attempt.SessionId)} error={ex.GetType().Name}");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                "无法确认上一笔刷卡结果。请联系主管确认 Linkly 后台状态后再继续。");
        }

        if (status is null)
        {
            return new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Checking, checkingMessage);
        }

        if (!IsFinal(status))
        {
            return new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Checking, checkingMessage);
        }

        if (IsApproved(status))
        {
            var result = await CompleteApprovedAttemptAsync(cart, session, settings, attempt, draft, status, cancellationToken);
            return result with { Message = "上一笔刷卡已成功，订单已自动恢复并保存。" };
        }

        if (IsDeclinedOrFailed(status))
        {
            cart.RestoreSnapshot(draft.CartSnapshot);
            await attemptRepository.UpdateOutcomeAsync(
                attempt.AttemptGuid,
                MapFailureStatus(status),
                status.ResponseCode,
                status.ResponseText,
                attempt.PaymentReference,
                DateTimeOffset.UtcNow,
                cancellationToken);
            await AcknowledgeAsync(settings, attempt, status, cancellationToken);
            var reason = string.IsNullOrWhiteSpace(status.ResponseText) ? status.Status : status.ResponseText;
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.DraftRestored,
                $"上一笔刷卡失败：{reason}。订单已恢复，请重新选择付款方式。");
        }

        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            "无法确认上一笔刷卡结果。请联系主管确认 Linkly 后台状态后再继续。");
    }

    private async Task<CardPaymentRecoveryResult> CompleteApprovedAttemptAsync(
        PosCartService cart,
        PosSessionState currentSession,
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt,
        CardPaymentOrderDraft draft,
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        cart.RestoreSnapshot(draft.CartSnapshot);
        var tenderAmount = draft.TxnType.Equals("R", StringComparison.OrdinalIgnoreCase)
            ? -Math.Abs(draft.CardAmount)
            : Math.Abs(draft.CardAmount);
        var cardTender = new PaymentTender(
            PaymentMethodKind.Card,
            tenderAmount,
            BuildPaymentReference(attempt, status),
            CardTransactions: [BuildCardTransaction(attempt, status, tenderAmount)],
            IdempotencyKey: $"CARD_ATTEMPT:{attempt.AttemptGuid:N}");
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

        await attemptRepository.UpdateOutcomeAsync(
            attempt.AttemptGuid,
            LocalCardPaymentAttemptStatus.Approved,
            status.ResponseCode,
            status.ResponseText,
            cardTender.Reference,
            DateTimeOffset.UtcNow,
            cancellationToken);
        await attemptRepository.MarkOrderCompletedAsync(attempt.AttemptGuid, DateTimeOffset.UtcNow, cancellationToken);
        await AcknowledgeAsync(settings, attempt, status, cancellationToken);
        cart.Clear();

        var pendingSyncCount = await syncQueueRepository.CountPendingAsync(cancellationToken);
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.OrderCompleted,
            string.Empty,
            order,
            tenders.Sum(tender => tender.Amount),
            checkoutResult.ChangeAmount,
            currentSession with { PendingSyncCount = pendingSyncCount });
    }

    private async Task AcknowledgeAsync(
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt,
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        await backendTerminalClient.AcknowledgeSessionAsync(settings, status.SessionId, cancellationToken);
        await attemptRepository.MarkAcknowledgedAsync(attempt.AttemptGuid, DateTimeOffset.UtcNow, cancellationToken);
    }

    private static CardPaymentOrderDraft DeserializeDraft(LocalCardPaymentAttempt attempt)
    {
        return JsonSerializer.Deserialize<CardPaymentOrderDraft>(attempt.OrderDraftJson, JsonOptions)
            ?? throw new InvalidOperationException("Card payment recovery draft is invalid.");
    }

    private static bool IsFinal(LinklyCloudBackendSessionResponse status)
    {
        return string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, StatusFailed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, StatusNotSubmitted, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApproved(LinklyCloudBackendSessionResponse status)
    {
        return string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(status.ResponseCode?.Trim(), "00", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeclinedOrFailed(LinklyCloudBackendSessionResponse status)
    {
        return IsFinal(status);
    }

    private static LocalCardPaymentAttemptStatus MapFailureStatus(LinklyCloudBackendSessionResponse status)
    {
        var text = $"{status.Status} {status.ResponseText}".ToUpperInvariant();
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

    private static string BuildPaymentReference(
        LocalCardPaymentAttempt attempt,
        LinklyCloudBackendSessionResponse status)
    {
        if (!string.IsNullOrWhiteSpace(attempt.PaymentReference))
        {
            return attempt.PaymentReference;
        }

        var txnRef = status.TxnRef ?? attempt.TxnRef ?? status.SessionId;
        return LinklyBackendPaymentReference.Format(txnRef, status.SessionId, status.Environment, refundReference: null);
    }

    private static CardTransactionDto BuildCardTransaction(
        LocalCardPaymentAttempt attempt,
        LinklyCloudBackendSessionResponse status,
        decimal amount)
    {
        return new CardTransactionDto(
            "ANZ",
            status.TxnRef ?? attempt.TxnRef ?? status.SessionId,
            null,
            null,
            null,
            null,
            null,
            status.ResponseCode,
            status.ResponseText,
            null,
            DateTimeOffset.UtcNow,
            Math.Abs(amount),
            status.ReceiptText);
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }
}
