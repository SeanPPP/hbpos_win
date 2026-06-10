using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;
using static Hbpos.Contracts.Linkly.LinklyCloudBackendStatusConstants;

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
    PosSessionState? UpdatedSession = null,
    CardPaymentRecoveryDialogDetails? DialogDetails = null)
{
    public static CardPaymentRecoveryResult None { get; } = new(CardPaymentRecoveryOutcome.None, string.Empty);
}

public sealed record CardPaymentRecoveryDialogDetails(
    string? SessionId,
    string? TxnRef,
    string? ResponseCode,
    string? ResponseText,
    decimal? Amount,
    DateTimeOffset Timestamp);

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
        LogRecoveryScan(settings, session, attempt);
        if (attempt is null)
        {
            LogRecoveryResult(settings, null, null, CardPaymentRecoveryOutcome.None, "no-open-attempt");
            return CardPaymentRecoveryResult.None;
        }

        if (attempt.Status == LocalCardPaymentAttemptStatus.RequiresReview)
        {
            ConsoleLog.Write(
                "CardRecovery",
                $"recover requires review attemptGuid={attempt.AttemptGuid} sessionId={LogValue(attempt.SessionId)} txnRef={LogValue(attempt.TxnRef)} amount={attempt.Amount:0.00}");
            LogRecoveryResult(settings, attempt, null, CardPaymentRecoveryOutcome.Unknown, "requires-review");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                "上一笔刷卡金额与订单金额不一致。请主管确认 Linkly 后台状态后处理。",
                DialogDetails: BuildDialogDetails(attempt));
        }

        if (attempt.Status == LocalCardPaymentAttemptStatus.OrderCompleted &&
            attempt.AcknowledgedAt is null &&
            !string.IsNullOrWhiteSpace(attempt.SessionId))
        {
            await RetryCompletedAttemptAcknowledgeAsync(settings, attempt, cancellationToken);
            LogRecoveryResult(settings, attempt, null, CardPaymentRecoveryOutcome.OrderCompleted, "order-completed-ack-retry");
            return new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.OrderCompleted, string.Empty);
        }

        var draft = DeserializeDraft(attempt);
        var checkingMessage = $"检测到上次程序关闭前有一笔 {attempt.Amount:C2} 刷卡交易正在处理，正在查询刷卡机状态。";
        await attemptRepository.MarkRecoveringAsync(attempt.AttemptGuid, DateTimeOffset.UtcNow, cancellationToken);
        LogRecoveryMarkedRecovering(settings, attempt);

        LinklyCloudBackendSessionResponse? status = null;
        var statusFromResumable = string.IsNullOrWhiteSpace(attempt.SessionId);
        try
        {
            status = !statusFromResumable
                ? await backendTerminalClient.GetSessionStatusAsync(settings, attempt.SessionId!, cancellationToken)
                : await backendTerminalClient.GetResumableSessionAsync(settings, cancellationToken);

            // 鏈?SessionId 浣嗗悗绔?session 宸茶繃鏈?娓呯悊锛屽厹搴曞皾璇?Resumable
            if (!statusFromResumable && status is null)
            {
                ConsoleLog.Write(
                    "CardRecovery",
                    $"recover session-status-null retrying-resumable attemptGuid={attempt.AttemptGuid} sessionId={LogValue(attempt.SessionId)}");
                status = await backendTerminalClient.GetResumableSessionAsync(settings, cancellationToken);
            }

            if (status is not null)
            {
                attempt = await BindRecoveredSessionAsync(attempt, status, cancellationToken);
            }

            if (status is not null && !IsFinal(status))
            {
                ConsoleLog.Write(
                    "CardRecovery",
                    $"recover pending resume start attemptGuid={attempt.AttemptGuid} sessionId={LogValue(status.SessionId)} txnRef={LogValue(status.TxnRef)} status={status.Status}");
                status = await backendTerminalClient.ResumeSessionUntilFinalAsync(settings, status, cancellationToken);
                attempt = await BindRecoveredSessionAsync(attempt, status, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.Write(
                "CardRecovery",
                $"recover status failed attemptGuid={attempt.AttemptGuid} sessionId={LogValue(attempt.SessionId)} error={ex.GetType().Name}");
            LogRecoveryResult(settings, attempt, status, CardPaymentRecoveryOutcome.Unknown, "status-query-failed", ex.GetType().Name);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                "无法确认上一笔刷卡结果。请联系主管确认 Linkly 后台状态后再继续。",
                DialogDetails: BuildDialogDetails(attempt, status));
        }

        if (status is null)
        {
            LogRecoveryResult(settings, attempt, null, CardPaymentRecoveryOutcome.Checking, "status-null");
            return new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Checking, checkingMessage);
        }

        if (!IsFinal(status))
        {
            LogRecoveryResult(settings, attempt, status, CardPaymentRecoveryOutcome.Checking, "remote-status-not-final");
            return new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Checking, checkingMessage);
        }

        if (!StatusMatchesAttempt(attempt, status, statusFromResumable, out var mismatchReason))
        {
            ConsoleLog.Write(
                "CardRecovery",
                $"recover status mismatch attemptGuid={attempt.AttemptGuid} sessionId={LogValue(attempt.SessionId)} statusSessionId={LogValue(status.SessionId)} txnRef={LogValue(attempt.TxnRef)} statusTxnRef={LogValue(status.TxnRef)} reason={mismatchReason}");
            LogRecoveryResult(settings, attempt, status, CardPaymentRecoveryOutcome.Unknown, mismatchReason);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                "无法确认上一笔刷卡结果。请联系主管确认 Linkly 后台状态后再继续。",
                DialogDetails: BuildDialogDetails(attempt, status));
        }

        if (!cart.IsEmpty)
        {
            ConsoleLog.Write(
                "CardRecovery",
                $"recover deferred current-cart-not-empty attemptGuid={attempt.AttemptGuid} sessionId={LogValue(attempt.SessionId)} statusSessionId={LogValue(status.SessionId)} outcome={status.Status}");
            LogRecoveryResult(settings, attempt, status, CardPaymentRecoveryOutcome.Unknown, "current-cart-not-empty");
            // 宸茶ˉ缁?session 鐨勬仮澶嶉」浠嶉渶淇濇寔 Recovering锛岄伩鍏嶇敤鎴锋竻绌鸿喘鐗╄溅鍚庢棤娉曞啀娆℃仮澶嶃€?
            await attemptRepository.MarkRecoveringAsync(attempt.AttemptGuid, DateTimeOffset.UtcNow, cancellationToken);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                "检测到上一笔刷卡结果需要处理，但当前购物车已有商品。请先完成或清空当前购物车后再恢复上一笔订单。",
                DialogDetails: BuildDialogDetails(attempt, status));
        }

        if (IsApproved(status))
        {
            var result = await CompleteApprovedAttemptAsync(cart, session, settings, attempt, draft, status, cancellationToken);
            LogRecoveryResult(settings, attempt, status, result.Outcome, "approved-order-completed");
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
            await TryAcknowledgeAsync(settings, attempt, status.SessionId, status.TxnRef, cancellationToken);
            var reason = string.IsNullOrWhiteSpace(status.ResponseText) ? status.Status : status.ResponseText;
            LogRecoveryResult(settings, attempt, status, CardPaymentRecoveryOutcome.DraftRestored, "declined-or-failed");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.DraftRestored,
                $"上一笔刷卡失败：{reason}。订单已恢复，请重新选择付款方式。",
                DialogDetails: BuildDialogDetails(attempt, status));
        }

        LogRecoveryResult(settings, attempt, status, CardPaymentRecoveryOutcome.Unknown, "unhandled-final-status");
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            "无法确认上一笔刷卡结果。请联系主管确认 Linkly 后台状态后再继续。",
            DialogDetails: BuildDialogDetails(attempt, status));
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
        await TryAcknowledgeAsync(settings, attempt, status.SessionId, status.TxnRef, cancellationToken);
        cart.Clear();

        var pendingSyncCount = await syncQueueRepository.CountPendingAsync(cancellationToken);
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.OrderCompleted,
            string.Empty,
            order,
            tenders.Sum(tender => tender.Amount),
            checkoutResult.ChangeAmount,
            currentSession with { PendingSyncCount = pendingSyncCount },
            BuildDialogDetails(attempt, status));
    }

    private static CardPaymentRecoveryDialogDetails BuildDialogDetails(
        LocalCardPaymentAttempt attempt,
        LinklyCloudBackendSessionResponse? status = null)
    {
        return new CardPaymentRecoveryDialogDetails(
            NormalizeOptional(status?.SessionId) ?? NormalizeOptional(attempt.SessionId),
            NormalizeOptional(status?.TxnRef) ?? NormalizeOptional(attempt.TxnRef),
            status?.ResponseCode,
            status?.ResponseText,
            attempt.Amount,
            DateTimeOffset.Now);
    }

    private async Task RetryCompletedAttemptAcknowledgeAsync(
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        ConsoleLog.Write(
            "CardRecovery",
            $"recover acknowledge retry attemptGuid={attempt.AttemptGuid} sessionId={LogValue(attempt.SessionId)} txnRef={LogValue(attempt.TxnRef)}");
        await TryAcknowledgeAsync(settings, attempt, attempt.SessionId!, attempt.TxnRef, cancellationToken);
    }

    private async Task<bool> TryAcknowledgeAsync(
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt,
        string sessionId,
        string? txnRef,
        CancellationToken cancellationToken)
    {
        try
        {
            await backendTerminalClient.AcknowledgeSessionAsync(settings, sessionId, cancellationToken);
            await attemptRepository.MarkAcknowledgedAsync(attempt.AttemptGuid, DateTimeOffset.UtcNow, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 鏈湴璁㈠崟/鑽夌鎭㈠宸茬粡瀹屾垚锛宎ck 澶辫触鍙奖鍝?backend 娓呯悊锛屼笉鑳介樆鏂惎鍔ㄤ綋楠屻€?
            ConsoleLog.Write(
                "CardRecovery",
                $"recover acknowledge failed attemptGuid={attempt.AttemptGuid} sessionId={LogValue(sessionId)} txnRef={LogValue(txnRef)} error={ex.GetType().Name}");
            return false;
        }
    }

    private async Task<LocalCardPaymentAttempt> BindRecoveredSessionAsync(
        LocalCardPaymentAttempt attempt,
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        if (!CanBindRecoveredSession(attempt, status))
        {
            return attempt;
        }

        var now = DateTimeOffset.UtcNow;
        await attemptRepository.UpdateSessionAsync(
            attempt.AttemptGuid,
            status.SessionId,
            status.TxnRef,
            now,
            cancellationToken);
        ConsoleLog.Write(
            "CardRecovery",
            $"recover session bound attemptGuid={attempt.AttemptGuid} sessionId={LogValue(status.SessionId)} txnRef={LogValue(status.TxnRef)} status={status.Status}");
        return attempt with
        {
            SessionId = status.SessionId,
            TxnRef = status.TxnRef ?? attempt.TxnRef,
            Status = LocalCardPaymentAttemptStatus.SessionStarted,
            UpdatedAt = now
        };
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

    private static bool CanBindRecoveredSession(
        LocalCardPaymentAttempt attempt,
        LinklyCloudBackendSessionResponse status)
    {
        if (!TextEquals(attempt.Environment, status.Environment) ||
            !TextEquals(attempt.StoreCode, status.StoreCode) ||
            !TextEquals(attempt.DeviceCode, status.DeviceCode))
        {
            return false;
        }

        var attemptSessionId = NormalizeOptional(attempt.SessionId);
        var statusSessionId = NormalizeOptional(status.SessionId);
        var attemptTxnRef = NormalizeOptional(attempt.TxnRef);
        var statusTxnRef = NormalizeOptional(status.TxnRef);

        if (attemptSessionId is not null && !TextEquals(attemptSessionId, statusSessionId))
        {
            return false;
        }

        if (attemptTxnRef is not null && statusTxnRef is not null && !TextEquals(attemptTxnRef, statusTxnRef))
        {
            return false;
        }

        if (attemptSessionId is not null && attemptTxnRef is null && statusTxnRef is not null)
        {
            return true;
        }

        return attemptTxnRef is not null &&
            attemptSessionId is null &&
            statusTxnRef is not null &&
            TextEquals(attemptTxnRef, statusTxnRef);
    }

    private static bool StatusMatchesAttempt(
        LocalCardPaymentAttempt attempt,
        LinklyCloudBackendSessionResponse status,
        bool statusFromResumable,
        out string mismatchReason)
    {
        if (!TextEquals(attempt.Environment, status.Environment))
        {
            mismatchReason = "environment-mismatch";
            return false;
        }

        var attemptSessionId = NormalizeOptional(attempt.SessionId);
        var statusSessionId = NormalizeOptional(status.SessionId);
        if (attemptSessionId is not null)
        {
            if (!TextEquals(attemptSessionId, statusSessionId))
            {
                mismatchReason = "session-id-mismatch";
                return false;
            }
        }
        else if (!statusFromResumable)
        {
            mismatchReason = "missing-attempt-session";
            return false;
        }

        var attemptTxnRef = NormalizeOptional(attempt.TxnRef);
        var statusTxnRef = NormalizeOptional(status.TxnRef);
        if (attemptTxnRef is not null)
        {
            if (statusTxnRef is null || !TextEquals(attemptTxnRef, statusTxnRef))
            {
                mismatchReason = "txn-ref-mismatch";
                return false;
            }
        }
        else if (attemptSessionId is null)
        {
            // 鏈湴娌℃湁 sessionId 鏃跺彧鑳介€氳繃 txnRef 缁戝畾 backend resumable锛岀己澶卞垯涓嶈兘鑷姩澶勭悊銆?
            mismatchReason = "missing-recoverable-binding";
            return false;
        }

        mismatchReason = string.Empty;
        return true;
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

    private static void LogRecoveryScan(
        CardTerminalSettings settings,
        PosSessionState session,
        LocalCardPaymentAttempt? attempt)
    {
        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "startup-recovery",
            "scan",
            environment: settings.Environment,
            sessionId: NormalizeOptional(attempt?.SessionId),
            success: attempt is not null,
            reason: attempt is null ? "no-open-attempt" : null,
            details: new
            {
                timestamp = DateTimeOffset.Now,
                pendingAttemptFound = attempt is not null,
                storeCode = session.StoreCode,
                deviceCode = session.DeviceCode,
                cashierId = session.CashierId,
                selectedEnvironment = settings.Environment.ToString(),
                certCase = "4.1.1",
                transactionReference = NormalizeOptional(attempt?.SessionId) ?? NormalizeOptional(attempt?.TxnRef),
                attemptGuid = attempt?.AttemptGuid,
                localStatus = attempt?.Status.ToString(),
                attemptSessionId = NormalizeOptional(attempt?.SessionId),
                txnRef = NormalizeOptional(attempt?.TxnRef),
                txnType = attempt?.TxnType,
                amount = attempt?.Amount,
                createdAt = attempt?.CreatedAt,
                updatedAt = attempt?.UpdatedAt
            });
    }

    private static void LogRecoveryMarkedRecovering(
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt)
    {
        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "startup-recovery",
            "marked-recovering",
            environment: settings.Environment,
            sessionId: NormalizeOptional(attempt.SessionId),
            details: new
            {
                timestamp = DateTimeOffset.Now,
                attemptGuid = attempt.AttemptGuid,
                certCase = "4.1.2",
                transactionReference = NormalizeOptional(attempt.SessionId) ?? NormalizeOptional(attempt.TxnRef),
                localStatus = attempt.Status.ToString(),
                attemptSessionId = NormalizeOptional(attempt.SessionId),
                txnRef = NormalizeOptional(attempt.TxnRef),
                txnType = attempt.TxnType,
                amount = attempt.Amount,
                storeCode = attempt.StoreCode,
                deviceCode = attempt.DeviceCode,
                cashierId = attempt.CashierId
            });
    }

    private static void LogRecoveryResult(
        CardTerminalSettings settings,
        LocalCardPaymentAttempt? attempt,
        LinklyCloudBackendSessionResponse? status,
        CardPaymentRecoveryOutcome outcome,
        string reason,
        string? error = null)
    {
        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "startup-recovery",
            "result",
            environment: settings.Environment,
            sessionId: NormalizeOptional(attempt?.SessionId) ?? NormalizeOptional(status?.SessionId),
            success: outcome is CardPaymentRecoveryOutcome.OrderCompleted or CardPaymentRecoveryOutcome.DraftRestored,
            reason: reason,
            response: status is null
                ? null
                : new
                {
                    environment = status.Environment,
                    storeCode = status.StoreCode,
                    deviceCode = status.DeviceCode,
                    sessionId = status.SessionId,
                    status = status.Status,
                    txnRef = NormalizeOptional(status.TxnRef),
                    responseCode = status.ResponseCode,
                    responseText = status.ResponseText,
                    recoveryAction = status.RecoveryAction,
                    lastHttpStatus = status.LastHttpStatus
                },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                outcome = outcome.ToString(),
                certCase = GetRecoveryCertificationCase(outcome, reason),
                error,
                attemptGuid = attempt?.AttemptGuid,
                transactionReference = NormalizeOptional(attempt?.SessionId) ??
                    NormalizeOptional(status?.SessionId) ??
                    NormalizeOptional(attempt?.TxnRef) ??
                    NormalizeOptional(status?.TxnRef),
                localStatus = attempt?.Status.ToString(),
                attemptSessionId = NormalizeOptional(attempt?.SessionId),
                statusSessionId = NormalizeOptional(status?.SessionId),
                txnRef = NormalizeOptional(attempt?.TxnRef),
                statusTxnRef = NormalizeOptional(status?.TxnRef),
                txnType = attempt?.TxnType,
                amount = attempt?.Amount,
                storeCode = attempt?.StoreCode ?? status?.StoreCode,
                deviceCode = attempt?.DeviceCode ?? status?.DeviceCode,
                cashierId = attempt?.CashierId,
                responseCode = status?.ResponseCode,
                responseText = status?.ResponseText
            });
    }

    private static string GetRecoveryCertificationCase(CardPaymentRecoveryOutcome outcome, string reason)
    {
        if (reason.Contains("approved", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("declined", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return "3.1.2/4.1.2";
        }

        return "4.1.2";
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool TextEquals(string? left, string? right)
    {
        return string.Equals(NormalizeOptional(left), NormalizeOptional(right), StringComparison.OrdinalIgnoreCase);
    }
}
