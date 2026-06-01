using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface ILinklyCloudTerminalClient
{
    Task<LinklyConnectionTestResult> TestConnectionAsync(
        CardTerminalSettings settings,
        string storeCode,
        string deviceCode,
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

public sealed class LinklyCloudTerminalClient(
    ILinklyCloudApiClient apiClient,
    ILinklyCloudSecretStore secretStore,
    TimeSpan? pollInterval = null,
    ILocalizationService? localization = null) : ILinklyCloudTerminalClient
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    private const string ProcessorName = "ANZ";
    private readonly TimeSpan _pollInterval = pollInterval.GetValueOrDefault(DefaultPollInterval);

    public async Task<LinklyConnectionTestResult> TestConnectionAsync(
        CardTerminalSettings settings,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken = default)
    {
        Log($"test start environment={settings.Environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} hasSecret={!string.IsNullOrWhiteSpace(settings.LinklyCloudSecret)} hasVendorId={!string.IsNullOrWhiteSpace(settings.LinklyPosVendorId)}");
        try
        {
            var endpointValidationMessage = ValidateEndpointSettings(settings);
            if (!string.IsNullOrWhiteSpace(endpointValidationMessage))
            {
                Log($"test blocked environment={settings.Environment} reason=invalid-endpoint");
                return new LinklyConnectionTestResult(false, endpointValidationMessage);
            }

            var token = await GetTokenAsync(settings, storeCode, deviceCode, cancellationToken);
            var result = await apiClient.SendLogonAsync(settings, token.Token, cancellationToken);
            var message = FormatResponseMessage(result.ResponseText, result.ResponseCode);
            Log($"test logon completed environment={settings.Environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} success={result.Succeeded} responseCode={LogValue(result.ResponseCode)}");
            return result.Succeeded
                ? new LinklyConnectionTestResult(true, string.IsNullOrWhiteSpace(message) ? T("linkly.cloud.test.success", "Linkly Cloud logon succeeded.") : message)
                : new LinklyConnectionTestResult(false, string.IsNullOrWhiteSpace(message) ? T("linkly.cloud.test.failed", "Linkly Cloud logon failed.") : message);
        }
        catch (LinklyCloudApiException ex)
        {
            Log($"test failed environment={settings.Environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} authFailure={ex.IsAuthenticationFailure} error={ex.GetType().Name}");
            return new LinklyConnectionTestResult(false, ex.IsAuthenticationFailure
                ? T("linkly.cloud.pairingInvalid", "Linkly Cloud pairing is invalid. Pair the terminal again.")
                : ex.Message);
        }
    }

    public Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        return RunTransactionAsync(
            "P",
            amount,
            session,
            settings,
            refundReference: null,
            cancellationToken);
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
            ? Task.FromResult(new PaymentAuthorizationResult(false, null, T("linkly.cloud.refundMissingReference", "Linkly Cloud refund requires an original RFN reference.")))
            : RunTransactionAsync(
                "R",
                amount,
                session,
                settings,
                refundReference,
                cancellationToken);
    }

    private async Task<PaymentAuthorizationResult> RunTransactionAsync(
        string txnType,
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? refundReference,
        CancellationToken cancellationToken)
    {
        if (amount <= 0m)
        {
            Log($"transaction blocked txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} reason=invalid-amount");
            return new PaymentAuthorizationResult(false, null, T("linkly.cloud.amountMustBePositive", "Card amount must be greater than zero."));
        }

        if (string.IsNullOrWhiteSpace(settings.LinklyCloudSecret))
        {
            Log($"transaction blocked txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} reason=missing-secret");
            return new PaymentAuthorizationResult(false, null, T("linkly.cloud.notPaired", "Linkly Cloud terminal is not paired."));
        }

        if (string.IsNullOrWhiteSpace(settings.LinklyPosVendorId))
        {
            Log($"transaction blocked txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} reason=missing-pos-vendor-id");
            return new PaymentAuthorizationResult(false, null, T("linkly.cloud.vendorIdMissing", "Linkly POS vendor id is not configured."));
        }

        var endpointValidationMessage = ValidateEndpointSettings(settings);
        if (!string.IsNullOrWhiteSpace(endpointValidationMessage))
        {
            Log($"transaction blocked txnType={txnType} environment={settings.Environment} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} reason=invalid-endpoint");
            return new PaymentAuthorizationResult(false, null, endpointValidationMessage);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(settings.TerminalTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(180) : settings.TerminalTimeout);

        try
        {
            Log($"transaction start environment={settings.Environment} txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} amountMinor={ToMinorUnits(amount)} hasRefundReference={!string.IsNullOrWhiteSpace(refundReference)}");
            var token = await GetTokenAsync(settings, session.StoreCode, session.DeviceCode, timeoutCts.Token);
            var txnRef = BuildTxnRef(session);
            var request = new LinklyCloudTransactionRequest(
                txnType,
                ToMinorUnits(amount),
                txnRef,
                string.IsNullOrWhiteSpace(refundReference)
                    ? null
                    : new Dictionary<string, string>
                    {
                        ["RFN"] = refundReference,
                        ["OPR"] = $"{session.CashierId}|{session.CashierName}",
                        ["AMT"] = ToMinorUnits(amount).ToString("D9", CultureInfo.InvariantCulture),
                        ["PCM"] = "0000"
                    });

            var result = await apiClient.SendTransactionAsync(settings, token.Token, request, timeoutCts.Token);
            if (IsPending(result))
            {
                Log($"transaction pending sessionId={result.SessionId} txnType={txnType} txnRef={txnRef}");
                var polled = await PollTransactionAsync(settings, session, token, result.SessionId, timeoutCts.Token);
                result = polled.Result;
                token = polled.Token;
            }

            if (result.Outcome == LinklyCloudTransactionOutcome.NotSubmitted)
            {
                Log($"transaction not-submitted retrying txnType={txnType} previousSessionId={result.SessionId} txnRef={txnRef}");
                result = await apiClient.SendTransactionAsync(settings, token.Token, request, timeoutCts.Token);
                if (IsPending(result))
                {
                    Log($"transaction retry pending sessionId={result.SessionId} txnType={txnType} txnRef={txnRef}");
                    result = (await PollTransactionAsync(settings, session, token, result.SessionId, timeoutCts.Token)).Result;
                }
            }

            if (result.Outcome == LinklyCloudTransactionOutcome.NotSubmitted)
            {
                Log($"transaction not-submitted final txnType={txnType} txnRef={txnRef}");
                return new PaymentAuthorizationResult(false, null, T("linkly.cloud.notSubmitted", "Linkly Cloud transaction was not submitted. Retry the payment."));
            }

            Log($"transaction completed txnType={txnType} sessionId={result.SessionId} txnRef={LogValue(result.TxnRef ?? txnRef)} approved={result.Succeeded && string.Equals(result.ResponseCode?.Trim(), "00", StringComparison.OrdinalIgnoreCase)} responseCode={LogValue(result.ResponseCode)} outcome={result.Outcome}");
            return ToAuthorizationResult(result, amount, txnRef);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Log($"transaction timed-out txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)}");
            return new PaymentAuthorizationResult(false, null, T("linkly.cloud.timeout", "Linkly Cloud transaction timed out."));
        }
        catch (LinklyCloudApiException ex)
        {
            Log($"transaction failed txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} authFailure={ex.IsAuthenticationFailure} error={ex.GetType().Name}");
            return new PaymentAuthorizationResult(false, null, ex.IsAuthenticationFailure
                ? T("linkly.cloud.pairingInvalid", "Linkly Cloud pairing is invalid. Pair the terminal again.")
                : ex.Message);
        }
        catch (JsonException)
        {
            Log($"transaction failed txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} reason=invalid-json");
            return new PaymentAuthorizationResult(false, null, T("linkly.cloud.invalidResponse", "Linkly Cloud returned an invalid response."));
        }
        catch (HttpRequestException)
        {
            Log($"transaction failed txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} reason=http-request-exception");
            return new PaymentAuthorizationResult(false, null, T("linkly.cloud.communicationFailed", "Linkly Cloud communication failed."));
        }
    }

    private async Task<PolledLinklyCloudTransaction> PollTransactionAsync(
        CardTerminalSettings settings,
        PosSessionState session,
        LinklyCloudToken token,
        string sessionId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pollInterval > TimeSpan.Zero)
            {
                await Task.Delay(_pollInterval, cancellationToken);
            }

            LinklyCloudTransactionResult result;
            try
            {
                result = await apiClient.GetTransactionAsync(settings, token.Token, sessionId, cancellationToken);
            }
            catch (LinklyCloudApiException ex) when (ex.IsAuthenticationFailure)
            {
                Log($"transaction status auth-failure refreshing-token sessionId={sessionId} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)}");
                token = await GetTokenAsync(settings, session.StoreCode, session.DeviceCode, cancellationToken);
                continue;
            }

            if (!IsPending(result))
            {
                Log($"transaction status resolved sessionId={sessionId} outcome={result.Outcome} success={result.Succeeded} responseCode={LogValue(result.ResponseCode)}");
                return new PolledLinklyCloudTransaction(result, token);
            }

            Log($"transaction status still-pending sessionId={sessionId}");
        }
    }

    private async Task<LinklyCloudToken> GetTokenAsync(
        CardTerminalSettings settings,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var posId = await secretStore.GetOrCreateLinklyCloudPosIdAsync(
            settings.Environment,
            storeCode,
            deviceCode,
            cancellationToken);
        Log($"token resolve start environment={settings.Environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} posId={ShortId(posId)}");
        return await apiClient.GetTokenAsync(settings, posId, cancellationToken);
    }

    private static string? ValidateEndpointSettings(CardTerminalSettings settings)
    {
        return CardTerminalSettings.ValidateLinklyCloudAuthBaseUrl(
                settings.Environment,
                settings.LinklyCloudAuthBaseUrl)
            ?? CardTerminalSettings.ValidateLinklyCloudRestBaseUrl(
                settings.Environment,
                settings.LinklyCloudRestBaseUrl);
    }

    private PaymentAuthorizationResult ToAuthorizationResult(
        LinklyCloudTransactionResult response,
        decimal requestedAmount,
        string requestedTxnRef)
    {
        var amount = response.Amount ?? requestedAmount;
        var txnRef = string.IsNullOrWhiteSpace(response.TxnRef) ? requestedTxnRef : response.TxnRef.Trim();
        var transaction = new CardTransactionDto(
            ProcessorName,
            txnRef,
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
            null);
        var approved = response.Succeeded &&
            string.Equals(response.ResponseCode?.Trim(), "00", StringComparison.OrdinalIgnoreCase);
        var reference = string.IsNullOrWhiteSpace(response.RefundReference)
            ? $"ANZCLOUD:{txnRef}"
            : $"ANZCLOUD:{txnRef}:{response.RefundReference.Trim()}";

        return approved
            ? new PaymentAuthorizationResult(true, reference, "ANZ Linkly Cloud", amount, [transaction])
            : new PaymentAuthorizationResult(false, reference, FormatResponseMessage(response.ResponseText, response.ResponseCode), amount, [transaction]);
    }

    private static bool IsPending(LinklyCloudTransactionResult result)
    {
        return result.Outcome == LinklyCloudTransactionOutcome.Pending ||
            (result.Outcome == LinklyCloudTransactionOutcome.Completed &&
            string.IsNullOrWhiteSpace(result.TxnRef) &&
            string.IsNullOrWhiteSpace(result.ResponseCode) &&
            string.IsNullOrWhiteSpace(result.ResponseText));
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

        var parts = reference.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 &&
            string.Equals(parts[0], "ANZCLOUD", StringComparison.OrdinalIgnoreCase)
                ? parts[2]
                : null;
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

    private string FormatResponseMessage(string? responseText, string? responseCode)
    {
        var text = NormalizeOptional(responseText);
        var code = NormalizeOptional(responseCode);
        if (text is null && code is null)
        {
            return T("linkly.cloud.declined", "ANZ Linkly Cloud transaction was declined.");
        }

        return code is null ? text! : $"{text ?? T("linkly.cloud.declined", "ANZ Linkly Cloud transaction was declined.")} ({code})";
    }

    private string T(string key, string fallback)
    {
        var value = localization?.T(key);
        return string.IsNullOrWhiteSpace(value) || value == $"[[{key}]]" ? fallback : value;
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
        ConsoleLog.Write("LinklyCloud", message);
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<null>";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 8 ? trimmed : $"{trimmed[..8]}...";
    }

    private sealed record PolledLinklyCloudTransaction(
        LinklyCloudTransactionResult Result,
        LinklyCloudToken Token);
}

public sealed class ConfiguredLinklyTerminalClient(
    LinklyTerminalClient localClient,
    ILinklyCloudTerminalClient cloudClient,
    ILinklyBackendTerminalClient? backendClient = null) : ILinklyTerminalClient
{
    public Task<LinklyConnectionTestResult> TestConnectionAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return localClient.TestConnectionAsync(host, port, timeout, cancellationToken);
    }

    public Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        // 运行时路由集中在终端适配层，付款流程只依赖统一的 Linkly client。
        return CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode) switch
        {
            LinklyConnectionMode.CloudDirectSync => cloudClient.PurchaseAsync(amount, session, settings, cancellationToken),
            LinklyConnectionMode.CloudBackendAsync => backendClient is null
                ? Task.FromResult(BackendUnavailable())
                : backendClient.PurchaseAsync(amount, session, settings, cancellationToken),
            _ => localClient.PurchaseAsync(amount, session, settings, cancellationToken)
        };
    }

    public Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        // 退款沿用同一分派规则，避免上层 UI 直接理解 Linkly 模式。
        return CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode) switch
        {
            LinklyConnectionMode.CloudDirectSync => cloudClient.RefundAsync(amount, session, settings, originalReference, cancellationToken),
            LinklyConnectionMode.CloudBackendAsync => backendClient is null
                ? Task.FromResult(BackendUnavailable())
                : backendClient.RefundAsync(amount, session, settings, originalReference, cancellationToken),
            _ => localClient.RefundAsync(amount, session, settings, originalReference, cancellationToken)
        };
    }

    public Task<PaymentAuthorizationResult> VoidAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        return localClient.VoidAsync(amount, session, settings, originalReference, cancellationToken);
    }

    private static PaymentAuthorizationResult BackendUnavailable()
    {
        return new PaymentAuthorizationResult(false, null, "ANZ Linkly backend terminal adapter is unavailable.");
    }
}
