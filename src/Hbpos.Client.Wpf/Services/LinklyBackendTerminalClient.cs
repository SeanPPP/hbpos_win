using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface ILinklyBackendTerminalClient
{
    Task<LinklyConnectionTestResult> TestConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default);
}

public sealed class LinklyBackendTerminalClient(
    HttpClient httpClient,
    ILinklyTerminalDialogService dialogService,
    TimeSpan? pollInterval = null,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
    ILocalizationService? localization = null) : ILinklyBackendTerminalClient
{
    private const string ProcessorName = "ANZ";
    private const string StatusCompleted = "Completed";
    private const string StatusFailed = "Failed";
    private const string StatusNotSubmitted = "NotSubmitted";
    private const string StatusTokenRefreshRequired = "TokenRefreshRequired";
    private const string RecoveryRetry = "Retry";
    private const string RecoveryRefreshToken = "RefreshToken";
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TimeSpan _pollInterval = pollInterval.GetValueOrDefault(DefaultPollInterval);
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync = delayAsync ?? Task.Delay;

    public async Task<LinklyConnectionTestResult> TestConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Log($"health request start environment={environment} componentVersion={GetComponentVersion()}");
            using var response = await httpClient.GetAsync(
                $"api/v1/linkly/cloud-backend/health?environment={Uri.EscapeDataString(environment.ToString())}",
                cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var health = ReadHealthResult(content);
                var failedCodes = GetFailedHealthChecks(health)
                    .Select(check => check.Code)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .ToArray();
                Log(
                    $"health request completed environment={environment} ready={health.IsReady} failedChecks={(failedCodes.Length == 0 ? "<none>" : string.Join(",", failedCodes))}");
                return health.IsReady
                    ? new LinklyConnectionTestResult(true, T("linkly.backend.configValid", "ANZ Linkly Cloud backend configuration is valid."))
                    : new LinklyConnectionTestResult(false, FormatHealthFailure(health));
            }

            var message = TryReadApiMessage(content) ??
                string.Format(
                    CultureInfo.InvariantCulture,
                    T("linkly.backend.configTestHttpFailed", "ANZ Linkly Cloud backend configuration test failed with HTTP {0}."),
                    (int)response.StatusCode);
            Log($"health request failed environment={environment} http={(int)response.StatusCode}");
            return new LinklyConnectionTestResult(false, message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log($"health request failed environment={environment} error={ex.GetType().Name}");
            return new LinklyConnectionTestResult(false, T("linkly.backend.communicationFailed", "ANZ Linkly Cloud backend communication failed."));
        }
    }

    public Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        return RunAsync("P", amount, session, settings, refundReference: null, cancellationToken);
    }

    public Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        var refundReference = TryParseRefundReference(originalReference);
        return string.IsNullOrWhiteSpace(refundReference)
            ? Task.FromResult(new PaymentAuthorizationResult(false, null, T("linkly.backend.refundMissingReference", "Linkly Cloud refund requires an original RFN reference.")))
            : RunAsync("R", amount, session, settings, refundReference, cancellationToken);
    }

    private async Task<PaymentAuthorizationResult> RunAsync(
        string txnType,
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? refundReference,
        CancellationToken cancellationToken)
    {
        if (amount <= 0m)
        {
            return new PaymentAuthorizationResult(false, null, T("linkly.backend.amountMustBePositive", "Card amount must be greater than zero."));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(settings.TerminalTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(180) : settings.TerminalTimeout);
        var keepDialogOpen = false;
        Log($"transaction request start txnType={txnType} environment={settings.Environment} componentVersion={GetComponentVersion()}");

        try
        {
            var fallbackTxnRef = BuildTxnRef(session);
            var activeStatus = await GetActiveSessionAsync(settings, timeoutCts.Token);
            if (activeStatus is not null)
            {
                var recoveredStatus = await ResumeActiveSessionAsync(settings, activeStatus, timeoutCts.Token);
                var recoveredResult = ToAuthorizationResult(recoveredStatus, amount, fallbackTxnRef, suppressPrintedReceipt: true);
                keepDialogOpen = !recoveredResult.Approved;
                return recoveredResult;
            }

            var request = new LinklyCloudBackendTransactionRequest(
                settings.Environment.ToString(),
                txnType,
                ToMinorUnits(amount),
                BuildPurchaseAnalysisData(amount, session, refundReference));

            LinklyCloudBackendSessionResponse status;
            try
            {
                status = await StartTransactionAsync(request, timeoutCts.Token);
            }
            catch (LinklyBackendHttpException ex) when (ex.HttpStatus == HttpStatusCode.Conflict)
            {
                // 409 代表终端已有 active session，不能把它折叠成普通通讯失败。
                activeStatus = await GetActiveSessionAsync(settings, timeoutCts.Token);
                if (activeStatus is null)
                {
                    var message = T("linkly.backend.activeSessionUnavailable", "Current terminal has an unfinished card transaction, but no recoverable active session was returned. Try again later.");
                    await PresentFinalFailureAsync("backend-active-unavailable", message, CancellationToken.None);
                    keepDialogOpen = true;
                    return new PaymentAuthorizationResult(false, null, message);
                }

                var recoveredStatus = await ResumeActiveSessionAsync(settings, activeStatus, timeoutCts.Token);
                var recoveredResult = ToAuthorizationResult(recoveredStatus, amount, fallbackTxnRef, suppressPrintedReceipt: true);
                keepDialogOpen = !recoveredResult.Approved;
                return recoveredResult;
            }

            status = await PollUntilFinalAsync(settings, status, timeoutCts.Token);
            var result = ToAuthorizationResult(status, amount, fallbackTxnRef, suppressPrintedReceipt: false);
            keepDialogOpen = !result.Approved;
            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var message = T("linkly.backend.timeout", "ANZ Linkly Cloud transaction timed out.");
            await PresentFinalFailureAsync("backend-timeout", message, CancellationToken.None);
            keepDialogOpen = true;
            return new PaymentAuthorizationResult(false, null, message);
        }
        catch (HttpRequestException)
        {
            var message = T("linkly.backend.communicationFailed", "ANZ Linkly Cloud backend communication failed.");
            await PresentFinalFailureAsync("backend-http-error", message, CancellationToken.None);
            keepDialogOpen = true;
            return new PaymentAuthorizationResult(false, null, message);
        }
        catch (JsonException)
        {
            var message = T("linkly.backend.invalidResponse", "ANZ Linkly Cloud backend returned an invalid response.");
            await PresentFinalFailureAsync("backend-json-error", message, CancellationToken.None);
            keepDialogOpen = true;
            return new PaymentAuthorizationResult(false, null, message);
        }
        finally
        {
            // 成功交易自动收起页面弹窗；失败最终状态保留给收银员确认。
            if (!keepDialogOpen)
            {
                await dialogService.CloseAsync(CancellationToken.None);
            }
        }
    }

    private async Task<LinklyCloudBackendSessionResponse> ResumeActiveSessionAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse activeStatus,
        CancellationToken cancellationToken)
    {
        var status = await PresentStatusAsync(
            settings,
            activeStatus,
            T("linkly.backend.activeSessionResume", "Current terminal has an unfinished card transaction. Continuing to poll/recover that session."),
            cancellationToken);
        if (!IsFinal(status))
        {
            status = await RecoverAsync(settings, status.SessionId, cancellationToken);
        }

        return await PollUntilFinalAsync(settings, status, cancellationToken);
    }

    private async Task<LinklyCloudBackendSessionResponse> PollUntilFinalAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        status = await PresentStatusAsync(settings, status, message: null, cancellationToken);
        while (!IsFinal(status))
        {
            await DelayBeforeNextPollAsync(status, cancellationToken);

            status = RequiresRecovery(status)
                ? await RecoverAsync(settings, status.SessionId, cancellationToken)
                : await GetStatusAsync(settings, status.SessionId, cancellationToken);
            status = await PresentStatusAsync(settings, status, message: null, cancellationToken);
        }

        if (string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) &&
            !HasReceipt(status))
        {
            for (var attempt = 0; attempt < 3 && !HasReceipt(status); attempt++)
            {
                await DelayAsync(_pollInterval, cancellationToken);

                status = await GetStatusAsync(settings, status.SessionId, cancellationToken);
                status = await PresentStatusAsync(settings, status, message: null, cancellationToken);
            }
        }

        return status;
    }

    private async Task<LinklyCloudBackendSessionResponse> PresentStatusAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse status,
        string? message,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var action = await dialogService.UpdateAsync(ToDialogState(status, message), cancellationToken);
            if (IsFinal(status) || action is null || string.IsNullOrWhiteSpace(action.Key))
            {
                return status;
            }

            // 刷卡机按键由独立对话服务翻译，支付流程只发送后端 sendkey 契约。
            try
            {
                status = await SendKeyAsync(settings, status.SessionId, action, cancellationToken);
                message = null;
            }
            catch (HttpRequestException)
            {
                message = T("linkly.backend.sendKeyFailed", "Card terminal action failed. Try again or recover the transaction.");
                try
                {
                    status = await GetStatusAsync(settings, status.SessionId, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    // 状态刷新失败时继续保留当前会话轮询，不能把一次无效按键升级成最终失败。
                }

                continue;
            }
        }
    }

    private Task PresentFinalFailureAsync(
        string sessionId,
        string message,
        CancellationToken cancellationToken)
    {
        return dialogService.UpdateAsync(
            new LinklyTerminalDialogState(
                sessionId,
                StatusFailed,
                message,
                ReceiptText: null,
                ResponseText: message,
                RecoveryCount: 0,
                LastHttpStatus: null,
                Message: null,
                IsInteractive: false,
                IsFinal: true,
                DisplayButtons: []),
            cancellationToken);
    }

    private static LinklyTerminalDialogState ToDialogState(
        LinklyCloudBackendSessionResponse status,
        string? message)
    {
        var isFinal = IsFinal(status);
        var responseText = NormalizeOptional(status.ResponseText);
        // 最终态不继续显示旧刷卡提示，避免盖住批准/失败结果。
        var displayText = isFinal
            ? responseText ?? NormalizeOptional(status.Status)
            : NormalizeOptional(status.DisplayText);

        return new LinklyTerminalDialogState(
            status.SessionId,
            status.Status,
            displayText,
            ReadReceiptText(status),
            responseText,
            status.RecoveryCount,
            status.LastHttpStatus,
            NormalizeOptional(message),
            LinklyTerminalDialogMode.CloudBackendInteractive,
            IsInteractive: !isFinal,
            IsFinal: isFinal,
            DisplayButtons: BuildDisplayButtons(status),
            InputType: NormalizeOptional(status.InputType),
            GraphicCode: NormalizeOptional(status.GraphicCode));
    }

    private static IReadOnlyList<LinklyTerminalDialogButton> BuildDisplayButtons(
        LinklyCloudBackendSessionResponse status)
    {
        if (IsFinal(status))
        {
            return [];
        }

        var buttons = new List<LinklyTerminalDialogButton>();
        if (!HasDisplayNotification(status))
        {
            return buttons;
        }

        // Linkly 官方 REST sendkey 将 OK 与 CANCEL 都映射为 Key=0，且一次只能显示一个。
        if (status.OKKeyFlag && status.CancelKeyFlag)
        {
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.okCancel", LinklyTerminalDialogKeys.OkCancel));
        }
        else if (status.OKKeyFlag)
        {
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.ok", LinklyTerminalDialogKeys.OkCancel));
        }

        if (status.AcceptYesKeyFlag)
        {
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.yesApproved", LinklyTerminalDialogKeys.Yes));
        }

        if (status.DeclineNoKeyFlag)
        {
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.noDeclined", LinklyTerminalDialogKeys.No, IsDestructive: true));
        }

        if (status.AuthoriseKeyFlag)
        {
            // 签名授权按官方 sendkey AUTH=3 发送，不能退化成普通确认。
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.authoriseSignature", LinklyTerminalDialogKeys.Auth));
        }

        if (!status.OKKeyFlag && status.CancelKeyFlag)
        {
            buttons.Add(CreateCancelButton());
        }

        return buttons;
    }

    private static LinklyTerminalDialogButton CreateCancelButton()
    {
        return new LinklyTerminalDialogButton(
            "linkly.backend.dialog.button.cancel",
            LinklyTerminalDialogKeys.OkCancel,
            IsDestructive: true);
    }

    private static bool HasDisplayNotification(LinklyCloudBackendSessionResponse status)
    {
        return (status.Notifications ?? [])
            .Any(notification => string.Equals(notification.Type, "display", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<LinklyCloudBackendSessionResponse> StartTransactionAsync(
        LinklyCloudBackendTransactionRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/v1/linkly/cloud-backend/transactions",
            request,
            JsonOptions,
            cancellationToken);
        return await ReadApiResultAsync(response, cancellationToken);
    }

    private async Task<LinklyCloudBackendSessionResponse?> GetActiveSessionAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/linkly/cloud-backend/transactions/active?environment={Uri.EscapeDataString(settings.Environment.ToString())}",
            cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : await ReadApiResultAsync(response, cancellationToken);
    }

    private async Task<LinklyCloudBackendSessionResponse> GetStatusAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/status?environment={Uri.EscapeDataString(settings.Environment.ToString())}",
            cancellationToken);
        return await ReadApiResultAsync(response, cancellationToken);
    }

    private async Task<LinklyCloudBackendSessionResponse> RecoverAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/recover",
            new LinklyCloudBackendRecoverRequest(settings.Environment.ToString()),
            JsonOptions,
            cancellationToken);
        return await ReadApiResultAsync(response, cancellationToken);
    }

    private async Task<LinklyCloudBackendSessionResponse> SendKeyAsync(
        CardTerminalSettings settings,
        string sessionId,
        LinklyTerminalDialogAction action,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/sendkey",
            new LinklyCloudBackendSendKeyRequest(
                settings.Environment.ToString(),
                LinklyTerminalDialogKeys.Normalize(action.Key),
                NormalizeOptional(action.Data)),
            JsonOptions,
            cancellationToken);
        return await ReadApiResultAsync(response, cancellationToken);
    }

    private static async Task<LinklyCloudBackendSessionResponse> ReadApiResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<LinklyCloudBackendSessionResponse>? result = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendSessionResponse>>(content, JsonOptions);
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                result = null;
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new LinklyBackendHttpException(
                result?.Message ?? $"Linkly backend request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode);
        }

        if (result?.Success != true || result.Data is null)
        {
            throw new LinklyBackendHttpException(
                result?.Message ?? "Linkly backend returned a failure response.",
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(result.Data.SessionId))
        {
            throw new JsonException("Linkly backend response is missing session id.");
        }

        return result.Data;
    }

    private static string? TryReadApiMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendSessionResponse>>(content, JsonOptions);
            return NormalizeOptional(result?.Message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LinklyCloudBackendHealthResponse ReadHealthResult(string content)
    {
        var result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendHealthResponse>>(content, JsonOptions);
        if (result?.Success != true || result.Data is null)
        {
            throw new JsonException("Linkly backend health response is invalid.");
        }

        return result.Data;
    }

    private string FormatHealthFailure(LinklyCloudBackendHealthResponse health)
    {
        var failedMessages = GetFailedHealthChecks(health)
            .Select(check => NormalizeOptional(check.Message))
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return failedMessages.Length == 0
            ? T("linkly.backend.configIncomplete", "ANZ Linkly Cloud backend configuration is incomplete.")
            : string.Join(Environment.NewLine, failedMessages!);
    }

    private static IReadOnlyList<LinklyCloudBackendHealthCheckDto> GetFailedHealthChecks(
        LinklyCloudBackendHealthResponse health)
    {
        return (health.Checks ?? [])
            .Where(check => !check.IsReady)
            .ToArray();
    }

    private PaymentAuthorizationResult ToAuthorizationResult(
        LinklyCloudBackendSessionResponse status,
        decimal requestedAmount,
        string requestedTxnRef,
        bool suppressPrintedReceipt)
    {
        if (string.Equals(status.Status, StatusNotSubmitted, StringComparison.OrdinalIgnoreCase))
        {
            return new PaymentAuthorizationResult(false, null, T("linkly.backend.notSubmitted", "Linkly Cloud transaction was not submitted. Retry the payment."));
        }

        var transactionResult = ReadTransactionResult(status, requestedAmount, requestedTxnRef);
        var amount = transactionResult.Amount ?? requestedAmount;
        var receiptText = ReadReceiptText(status, suppressPrintedReceipt);
        var transaction = ToCardTransaction(transactionResult, amount, receiptText);
        var approved = string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) &&
            transactionResult.Succeeded &&
            string.Equals(transactionResult.ResponseCode?.Trim(), "00", StringComparison.OrdinalIgnoreCase);
        var reference = LinklyBackendPaymentReference.Format(
            transaction.TxnRef ?? transactionResult.SessionId,
            transactionResult.SessionId,
            status.Environment,
            transactionResult.RefundReference);

        return approved
            ? new PaymentAuthorizationResult(true, reference, "ANZ Linkly Cloud", amount, [transaction])
            : new PaymentAuthorizationResult(false, reference, FormatResponseMessage(transactionResult.ResponseText, transactionResult.ResponseCode), amount, [transaction]);
    }

    private static CardTransactionDto ToCardTransaction(
        LinklyCloudTransactionResult response,
        decimal amount,
        string? receiptText)
    {
        return new CardTransactionDto(
            ProcessorName,
            NormalizeOptional(response.TxnRef) ?? response.SessionId,
            NormalizeOptional(response.AuthCode),
            NormalizeOptional(response.CardType),
            int.TryParse(response.CardName, out var cardName) && cardName > 0 ? cardName : null,
            MaskCardNumber(response.Pan),
            NormalizeOptional(response.Caid),
            NormalizeOptional(response.ResponseCode),
            NormalizeOptional(response.ResponseText),
            NormalizeOptional(response.Stan),
            null,
            decimal.Round(amount, 2, MidpointRounding.AwayFromZero),
            NormalizeOptional(receiptText));
    }

    private static LinklyCloudTransactionResult ReadTransactionResult(
        LinklyCloudBackendSessionResponse status,
        decimal requestedAmount,
        string requestedTxnRef)
    {
        var protectedResponseCode = NormalizeOptional(status.ResponseCode);
        var protectedResponseText = NormalizeOptional(status.ResponseText);
        var notifications = status.Notifications ?? [];
        var transactionNotification = string.IsNullOrWhiteSpace(protectedResponseCode)
            ? notifications.LastOrDefault(IsTransactionNotification)
            : notifications.LastOrDefault(notification =>
                IsTransactionNotification(notification) &&
                TransactionNotificationMatchesProtectedResult(notification, protectedResponseCode, protectedResponseText));
        if (transactionNotification is null || string.IsNullOrWhiteSpace(transactionNotification.PayloadJson))
        {
            return new LinklyCloudTransactionResult(
                status.SessionId,
                string.Equals(protectedResponseCode, "00", StringComparison.OrdinalIgnoreCase),
                NormalizeOptional(status.TxnRef) ?? requestedTxnRef,
                null,
                null,
                null,
                null,
                null,
                protectedResponseCode,
                protectedResponseText,
                null,
                requestedAmount,
                null);
        }

        using var document = JsonDocument.Parse(transactionNotification.PayloadJson);
        var response = ReadResponse(document.RootElement);
        var purchaseAnalysisData = ReadObject(response, "PurchaseAnalysisData");
        return new LinklyCloudTransactionResult(
            status.SessionId,
            string.IsNullOrWhiteSpace(protectedResponseCode)
                ? ReadBool(response, "Success") == true
                : string.Equals(protectedResponseCode, "00", StringComparison.OrdinalIgnoreCase),
            NormalizeOptional(status.TxnRef) ?? requestedTxnRef,
            ReadString(response, "AuthCode"),
            ReadString(response, "CardType"),
            ReadString(response, "CardName"),
            ReadString(response, "Pan"),
            ReadString(response, "Caid"),
            protectedResponseCode ?? ReadString(response, "ResponseCode"),
            protectedResponseText ?? ReadString(response, "ResponseText"),
            ReadString(response, "Stan"),
            ReadDecimal(response, "AmtPurchase") ?? requestedAmount,
            ReadString(purchaseAnalysisData, "RFN"));
    }

    private static bool IsTransactionNotification(LinklyCloudBackendNotificationDto notification)
    {
        return string.Equals(notification.Type, "transaction", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TransactionNotificationMatchesProtectedResult(
        LinklyCloudBackendNotificationDto notification,
        string protectedResponseCode,
        string? protectedResponseText)
    {
        if (string.IsNullOrWhiteSpace(notification.PayloadJson))
        {
            return false;
        }

        using var document = JsonDocument.Parse(notification.PayloadJson);
        var response = ReadResponse(document.RootElement);
        if (!string.Equals(ReadString(response, "ResponseCode"), protectedResponseCode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var responseText = ReadString(response, "ResponseText");
        return string.IsNullOrWhiteSpace(protectedResponseText) ||
            string.IsNullOrWhiteSpace(responseText) ||
            string.Equals(responseText, protectedResponseText, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadReceiptText(LinklyCloudBackendSessionResponse status)
    {
        return ReadReceiptText(status, suppressPrintedReceipt: false);
    }

    private static string? ReadReceiptText(
        LinklyCloudBackendSessionResponse status,
        bool suppressPrintedReceipt)
    {
        if (suppressPrintedReceipt && status.ReceiptPrintedAt is not null)
        {
            return null;
        }

        return NormalizeOptional(status.ReceiptText) ?? ReadReceiptText(status.Notifications ?? []);
    }

    private static string? ReadReceiptText(IReadOnlyList<LinklyCloudBackendNotificationDto> notifications)
    {
        var receipts = notifications
            .Where(notification => string.Equals(notification.Type, "receipt", StringComparison.OrdinalIgnoreCase))
            .Select(notification => ReadReceiptNotification(notification.PayloadJson))
            .Where(receipt => !string.IsNullOrWhiteSpace(receipt))
            .Select(receipt => receipt!)
            .ToArray();
        return receipts.Length == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, receipts);
    }

    private static bool HasReceipt(LinklyCloudBackendSessionResponse status)
    {
        return !string.IsNullOrWhiteSpace(status.ReceiptText) ||
            (status.Notifications ?? []).Any(notification =>
                string.Equals(notification.Type, "receipt", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(ReadReceiptNotification(notification.PayloadJson)));
    }

    private static string? ReadReceiptNotification(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payloadJson);
        return ReadReceiptText(document.RootElement) ?? ReadReceiptText(ReadResponse(document.RootElement));
    }

    private static string? ReadReceiptText(JsonElement element)
    {
        if (!TryGetProperty(element, "ReceiptText", out var receipt))
        {
            return null;
        }

        if (receipt.ValueKind == JsonValueKind.String)
        {
            return NormalizeOptional(receipt.GetString());
        }

        if (receipt.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var lines = receipt
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => NormalizeOptional(item.GetString()))
            .Where(line => line is not null)
            .Select(line => line!)
            .ToArray();
        return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static bool IsFinal(LinklyCloudBackendSessionResponse status)
    {
        return string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, StatusFailed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, StatusNotSubmitted, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresRecovery(LinklyCloudBackendSessionResponse status)
    {
        return string.Equals(status.Status, StatusTokenRefreshRequired, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.RecoveryAction, RecoveryRefreshToken, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(status.RecoveryAction, RecoveryRetry, StringComparison.OrdinalIgnoreCase) &&
                IsRecoveryHttpStatus(status.LastHttpStatus));
    }

    private async Task DelayBeforeNextPollAsync(
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        await DelayAsync(GetNextPollDelay(status), cancellationToken);
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await _delayAsync(delay, cancellationToken);
        }
    }

    private TimeSpan GetNextPollDelay(LinklyCloudBackendSessionResponse status)
    {
        if (!IsRecoveryHttpStatus(status.LastHttpStatus))
        {
            return _pollInterval;
        }

        var exponent = Math.Clamp(status.RecoveryCount, 0, 6);
        var multiplier = 1 << exponent;
        var milliseconds = Math.Min(
            _pollInterval.TotalMilliseconds * multiplier,
            TimeSpan.FromSeconds(30).TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static bool IsRecoveryHttpStatus(int? httpStatus)
    {
        return httpStatus == (int)HttpStatusCode.RequestTimeout ||
            httpStatus is >= 500 and <= 599;
    }

    private static IReadOnlyDictionary<string, string>? BuildPurchaseAnalysisData(
        decimal amount,
        PosSessionState session,
        string? refundReference)
    {
        if (string.IsNullOrWhiteSpace(refundReference))
        {
            return null;
        }

        return new Dictionary<string, string>
        {
            ["RFN"] = refundReference.Trim(),
            ["OPR"] = $"{session.CashierId}|{session.CashierName}",
            ["AMT"] = ToMinorUnits(amount).ToString("D9", CultureInfo.InvariantCulture),
            ["PCM"] = "0000"
        };
    }

    private static long ToMinorUnits(decimal amount)
    {
        return decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
    }

    private static string BuildTxnRef(PosSessionState session)
    {
        var device = new string(session.DeviceCode.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(device))
        {
            device = "POS";
        }

        return Limit($"{device}{DateTimeOffset.UtcNow:yyMMddHHmmss}", 16);
    }

    private static string? TryParseRefundReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var backendRefundReference = LinklyBackendPaymentReference.TryGetRefundReference(reference);
        if (!string.IsNullOrWhiteSpace(backendRefundReference))
        {
            return backendRefundReference;
        }

        var parts = reference.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 &&
            string.Equals(parts[0], "ANZCLOUD", StringComparison.OrdinalIgnoreCase)
                ? parts[2]
                : null;
    }

    private string FormatResponseMessage(string? responseText, string? responseCode)
    {
        var text = NormalizeOptional(responseText);
        var code = NormalizeOptional(responseCode);
        if (text is null && code is null)
        {
            return T("linkly.backend.declined", "ANZ Linkly Cloud transaction was declined.");
        }

        return code is null ? text! : $"{text ?? T("linkly.backend.declined", "ANZ Linkly Cloud transaction was declined.")} ({code})";
    }

    private static JsonElement ReadResponse(JsonElement root)
    {
        return TryGetProperty(root, "Response", out var response) && response.ValueKind == JsonValueKind.Object
            ? response
            : root;
    }

    private static JsonElement ReadObject(JsonElement root, string propertyName)
    {
        return TryGetProperty(root, propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => NormalizeOptional(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var decimalValue))
        {
            return decimalValue / 100m;
        }

        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out var parsed)
            ? parsed / 100m
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? MaskCardNumber(string? pan)
    {
        var value = NormalizeOptional(pan);
        if (value is null)
        {
            return null;
        }

        if (value.Contains('*', StringComparison.Ordinal) || value.Contains('X', StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length <= 4 ? digits : $"****{digits[^4..]}";
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string Limit(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static void Log(string message)
    {
        ConsoleLog.Write("LinklyBackend", message);
    }

    private static string GetComponentVersion()
    {
        var assembly = typeof(LinklyBackendTerminalClient).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            assembly.GetName().Version?.ToString() ??
            "unknown";
    }

    private string T(string key, string fallback)
    {
        var value = localization?.T(key) ?? LocalizationResourceProvider.Instance[key];
        return string.IsNullOrWhiteSpace(value) || value.StartsWith("[[", StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private sealed class LinklyBackendHttpException(
        string message,
        HttpStatusCode httpStatus) : HttpRequestException(message)
    {
        public HttpStatusCode HttpStatus { get; } = httpStatus;
    }
}
