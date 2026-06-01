using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null) : ILinklyBackendTerminalClient
{
    private const string ProcessorName = "ANZ";
    private const string StatusCompleted = "Completed";
    private const string StatusFailed = "Failed";
    private const string StatusNotSubmitted = "NotSubmitted";
    private const string StatusTokenRefreshRequired = "TokenRefreshRequired";
    private const string RecoveryRetry = "Retry";
    private const string RecoveryRefreshToken = "RefreshToken";
    private const string ActiveSessionMessage = "当前终端有未完成刷卡交易，正在继续轮询/恢复该 session。";
    private const string ActiveSessionUnavailableMessage = "当前终端有未完成刷卡交易，但没有取得可恢复的 active session，请稍后重试。";
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
            using var response = await httpClient.GetAsync(
                $"api/v1/linkly/cloud-backend/health?environment={Uri.EscapeDataString(environment.ToString())}",
                cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var health = ReadHealthResult(content);
                return health.IsReady
                    ? new LinklyConnectionTestResult(true, "ANZ Linkly Cloud backend configuration is valid.")
                    : new LinklyConnectionTestResult(false, FormatHealthFailure(health));
            }

            var message = TryReadApiMessage(content) ??
                $"ANZ Linkly Cloud backend configuration test failed with HTTP {(int)response.StatusCode}.";
            return new LinklyConnectionTestResult(false, message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new LinklyConnectionTestResult(false, "ANZ Linkly Cloud backend communication failed.");
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
            ? Task.FromResult(new PaymentAuthorizationResult(false, null, "Linkly Cloud refund requires an original RFN reference."))
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
            return new PaymentAuthorizationResult(false, null, "Card amount must be greater than zero.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(settings.TerminalTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(180) : settings.TerminalTimeout);

        try
        {
            var fallbackTxnRef = BuildTxnRef(session);
            var activeStatus = await GetActiveSessionAsync(settings, timeoutCts.Token);
            if (activeStatus is not null)
            {
                var recoveredStatus = await ResumeActiveSessionAsync(settings, activeStatus, timeoutCts.Token);
                var recoveredResult = ToAuthorizationResult(recoveredStatus, amount, fallbackTxnRef, suppressPrintedReceipt: true);
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
                    return new PaymentAuthorizationResult(false, null, ActiveSessionUnavailableMessage);
                }

                var recoveredStatus = await ResumeActiveSessionAsync(settings, activeStatus, timeoutCts.Token);
                var recoveredResult = ToAuthorizationResult(recoveredStatus, amount, fallbackTxnRef, suppressPrintedReceipt: true);
                return recoveredResult;
            }

            status = await PollUntilFinalAsync(settings, status, timeoutCts.Token);
            var result = ToAuthorizationResult(status, amount, fallbackTxnRef, suppressPrintedReceipt: false);
            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new PaymentAuthorizationResult(false, null, "ANZ Linkly Cloud transaction timed out.");
        }
        catch (HttpRequestException)
        {
            return new PaymentAuthorizationResult(false, null, "ANZ Linkly Cloud backend communication failed.");
        }
        catch (JsonException)
        {
            return new PaymentAuthorizationResult(false, null, "ANZ Linkly Cloud backend returned an invalid response.");
        }
        finally
        {
            await dialogService.CloseAsync(CancellationToken.None);
        }
    }

    private async Task<LinklyCloudBackendSessionResponse> ResumeActiveSessionAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse activeStatus,
        CancellationToken cancellationToken)
    {
        var status = await PresentStatusAsync(settings, activeStatus, ActiveSessionMessage, cancellationToken);
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
            status = await SendKeyAsync(settings, status.SessionId, action, cancellationToken);
            message = null;
        }
    }

    private static LinklyTerminalDialogState ToDialogState(
        LinklyCloudBackendSessionResponse status,
        string? message)
    {
        return new LinklyTerminalDialogState(
            status.SessionId,
            status.Status,
            NormalizeOptional(status.DisplayText),
            ReadReceiptText(status),
            NormalizeOptional(status.ResponseText),
            status.RecoveryCount,
            status.LastHttpStatus,
            NormalizeOptional(message));
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

    private static string FormatHealthFailure(LinklyCloudBackendHealthResponse health)
    {
        var failedCheck = (health.Checks ?? [])
            .FirstOrDefault(check => !check.IsReady && !string.IsNullOrWhiteSpace(check.Message));
        return failedCheck?.Message ?? "ANZ Linkly Cloud backend configuration is incomplete.";
    }

    private PaymentAuthorizationResult ToAuthorizationResult(
        LinklyCloudBackendSessionResponse status,
        decimal requestedAmount,
        string requestedTxnRef,
        bool suppressPrintedReceipt)
    {
        if (string.Equals(status.Status, StatusNotSubmitted, StringComparison.OrdinalIgnoreCase))
        {
            return new PaymentAuthorizationResult(false, null, "Linkly Cloud transaction was not submitted. Retry the payment.");
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
        var notifications = status.Notifications ?? [];
        var transactionNotification = notifications
            .LastOrDefault(notification => string.Equals(notification.Type, "transaction", StringComparison.OrdinalIgnoreCase));
        if (transactionNotification is null || string.IsNullOrWhiteSpace(transactionNotification.PayloadJson))
        {
            return new LinklyCloudTransactionResult(
                status.SessionId,
                string.Equals(status.ResponseCode?.Trim(), "00", StringComparison.OrdinalIgnoreCase),
                NormalizeOptional(status.TxnRef) ?? requestedTxnRef,
                null,
                null,
                null,
                null,
                null,
                NormalizeOptional(status.ResponseCode),
                NormalizeOptional(status.ResponseText),
                null,
                requestedAmount,
                null);
        }

        using var document = JsonDocument.Parse(transactionNotification.PayloadJson);
        var response = ReadResponse(document.RootElement);
        var purchaseAnalysisData = ReadObject(response, "PurchaseAnalysisData");
        return new LinklyCloudTransactionResult(
            status.SessionId,
            ReadBool(response, "Success") == true,
            NormalizeOptional(status.TxnRef) ?? requestedTxnRef,
            ReadString(response, "AuthCode"),
            ReadString(response, "CardType"),
            ReadString(response, "CardName"),
            ReadString(response, "Pan"),
            ReadString(response, "Caid"),
            ReadString(response, "ResponseCode") ?? NormalizeOptional(status.ResponseCode),
            ReadString(response, "ResponseText") ?? NormalizeOptional(status.ResponseText),
            ReadString(response, "Stan"),
            ReadDecimal(response, "AmtPurchase") ?? requestedAmount,
            ReadString(purchaseAnalysisData, "RFN"));
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
            return "ANZ Linkly Cloud transaction was declined.";
        }

        return code is null ? text! : $"{text ?? "ANZ Linkly Cloud transaction was declined."} ({code})";
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

    private sealed class LinklyBackendHttpException(
        string message,
        HttpStatusCode httpStatus) : HttpRequestException(message)
    {
        public HttpStatusCode HttpStatus { get; } = httpStatus;
    }
}
