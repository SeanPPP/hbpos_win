using System.Globalization;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;
using PCEFTPOS.EFTClient.IPInterface;

namespace Hbpos.Client.Wpf.Services;

public interface ILinklyTerminalClient
{
    Task<LinklyConnectionTestResult> TestConnectionAsync(
        string host,
        int port,
        TimeSpan timeout,
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

    Task<PaymentAuthorizationResult> VoidAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default);
}

public sealed record LinklyConnectionTestResult(bool Succeeded, string? Message = null);

public interface ILinklyEftClientFactory
{
    ILinklyEftClient Create();
}

public interface ILinklyEftClient : IDisposable
{
    Task<bool> ConnectAsync(
        string hostName,
        int hostPort,
        bool useSsl,
        bool useKeepAlive);

    Task<bool> WriteRequestAsync(EFTRequest request);

    Task<bool> SendCancelRequestAsync();

    Task<EFTResponse?> ReadResponseAsync(CancellationToken cancellationToken);

    bool Disconnect();
}

public sealed class LinklyEftClientFactory : ILinklyEftClientFactory
{
    public ILinklyEftClient Create()
    {
        return new LinklyEftClientAdapter(new EFTClientIPAsync());
    }
}

public sealed class LinklyEftClientAdapter(EFTClientIPAsync client) : ILinklyEftClient
{
    public Task<bool> ConnectAsync(
        string hostName,
        int hostPort,
        bool useSsl,
        bool useKeepAlive)
    {
        return client.ConnectAsync(hostName, hostPort, useSsl, useKeepAlive);
    }

    public Task<bool> WriteRequestAsync(EFTRequest request)
    {
        return client.WriteRequestAsync(request);
    }

    public Task<bool> SendCancelRequestAsync()
    {
        return client.WriteRequestAsync(new EFTSendKeyRequest { Key = EFTPOSKey.OkCancel });
    }

    public async Task<EFTResponse?> ReadResponseAsync(CancellationToken cancellationToken)
    {
        return await client.ReadResponseAsync(cancellationToken);
    }

    public bool Disconnect()
    {
        return client.Disconnect();
    }

    public void Dispose()
    {
        client.Dispose();
    }
}

public sealed class LinklyTerminalClient(
    ILinklyEftClientFactory clientFactory,
    ILocalizationService? localization = null) : ILinklyTerminalClient
{
    private const string LogCategory = "LinklyLocal";
    private const string LogSource = "local-ip";
    private const string ProcessorName = "ANZ";
    private const string Merchant = "00";
    private const string CancelledMessage = "ANZ Linkly transaction was cancelled.";

    public async Task<LinklyConnectionTestResult> TestConnectionAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CreateTimeoutToken(timeout, cancellationToken);
        using var client = clientFactory.Create();
        try
        {
            var connectRequest = CreateConnectRequest(host, port);
            LogJson(
                "connect",
                "start",
                "request",
                request: connectRequest);
            var connected = await client.ConnectAsync(host, port, useSsl: false, useKeepAlive: false)
                .WaitAsync(timeoutCts.Token);
            LogJson(
                "connect",
                connected ? "succeeded" : "failed",
                "response",
                success: connected,
                request: connectRequest,
                response: new
                {
                    connected
                });
            return connected
                ? new LinklyConnectionTestResult(true, T("linkly.local.test.success", "Linkly EFT-Client connection succeeded."))
                : new LinklyConnectionTestResult(false, T("linkly.local.test.failed", "Linkly EFT-Client connection failed."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogJson(
                "connect",
                "failed",
                "response",
                success: false,
                reason: "timeout");
            return new LinklyConnectionTestResult(false, T("linkly.local.test.timeout", "Linkly EFT-Client connection timed out."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJson(
                "connect",
                "failed",
                "response",
                success: false,
                reason: ex.GetType().Name,
                details: new
                {
                    ex.Message
                });
            return new LinklyConnectionTestResult(
                false,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("linkly.local.test.exception", "Linkly connection failed: {0}"),
                    ex.Message));
        }
        finally
        {
            SafeDisconnect(client, txnRef: null);
        }
    }

    public Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        return RunTransactionAsync(
            TransactionType.PurchaseCash,
            amount,
            session,
            settings,
            originalReference: null,
            cancellationToken);
    }

    public Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        return RunTransactionAsync(
            TransactionType.Refund,
            amount,
            session,
            settings,
            originalReference,
            cancellationToken);
    }

    public Task<PaymentAuthorizationResult> VoidAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        return RunTransactionAsync(
            TransactionType.Void,
            amount,
            session,
            settings,
            originalReference,
            cancellationToken);
    }

    private async Task<PaymentAuthorizationResult> RunTransactionAsync(
        TransactionType transactionType,
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken)
    {
        if (amount <= 0m)
        {
            return new PaymentAuthorizationResult(false, null, T("linkly.local.amountMustBePositive", "Card amount must be greater than zero."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new PaymentAuthorizationResult(false, null, T("linkly.local.cancelled", CancelledMessage));
        }

        using var timeoutCts = CreateTimeoutToken(settings.TerminalTimeout, cancellationToken);
        var txnRef = NormalizeReference(originalReference) ?? BuildTxnRef(session);
        var request = CreateTransactionRequest(transactionType, amount, txnRef);
        var receipts = new List<string>();
        using var client = clientFactory.Create();
        var transactionRequestSent = false;

        try
        {
            var connectRequest = CreateConnectRequest(settings.LinklyHost, settings.LinklyPort);
            LogJson(
                "connect",
                "start",
                "request",
                settings.Environment,
                txnRef,
                request: connectRequest);
            var connected = await client.ConnectAsync(settings.LinklyHost, settings.LinklyPort, useSsl: false, useKeepAlive: false)
                .WaitAsync(timeoutCts.Token);
            LogJson(
                "connect",
                connected ? "succeeded" : "failed",
                "response",
                settings.Environment,
                txnRef,
                success: connected,
                request: connectRequest,
                response: new
                {
                    connected
                });
            if (!connected)
            {
                return new PaymentAuthorizationResult(false, null, T("linkly.local.connectionFailed", "ANZ Linkly EFT-Client connection failed."));
            }

            // 保留终端原始交易报文，方便把 POS 请求与终端回包按 TxnRef 串起来排查。
            LogJson(
                "transaction",
                "sent",
                "request",
                settings.Environment,
                txnRef,
                request: request);
            if (!await client.WriteRequestAsync(request).WaitAsync(timeoutCts.Token))
            {
                LogJson(
                    "transaction",
                    "failed",
                    "response",
                    settings.Environment,
                    txnRef,
                    success: false,
                    reason: "write-request-failed",
                    request: request);
                return new PaymentAuthorizationResult(false, null, T("linkly.local.requestSendFailed", "ANZ Linkly transaction request could not be sent."));
            }

            transactionRequestSent = true;
            var response = await ReadTransactionResponseAsync(client, receipts, timeoutCts.Token, settings.Environment, txnRef);
            return ToAuthorizationResult(response, amount, txnRef, receipts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogJson(
                "transaction",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: "caller-cancelled",
                request: request);
            if (transactionRequestSent)
            {
                return await TryCancelActiveTransactionAsync(
                    client,
                    settings,
                    amount,
                    txnRef,
                    receipts);
            }

            return new PaymentAuthorizationResult(false, null, T("linkly.local.cancelled", CancelledMessage));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionException)
        {
            var fallbackMessage = ex is OperationCanceledException
                ? T("linkly.local.timeout", "ANZ Linkly transaction timed out.")
                : T("linkly.local.connectionClosed", "ANZ Linkly connection was closed.");
            LogJson(
                "transaction",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: ex is OperationCanceledException ? "timeout" : "connection-closed",
                request: request,
                details: new
                {
                    exception = ex.GetType().Name,
                    ex.Message
                });

            if (!transactionRequestSent)
            {
                return new PaymentAuthorizationResult(false, null, fallbackMessage);
            }

            return await TryRecoverLastTransactionAsync(
                settings,
                amount,
                txnRef,
                receipts,
                fallbackMessage,
                cancellationToken);
        }
        catch (Exception ex)
        {
            var fallbackMessage = string.Format(
                CultureInfo.CurrentCulture,
                T("linkly.local.transactionFailed", "ANZ Linkly transaction failed: {0}"),
                ex.Message);
            LogJson(
                "transaction",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: ex.GetType().Name,
                request: request,
                details: new
                {
                    ex.Message
                });
            if (transactionRequestSent)
            {
                return await TryRecoverLastTransactionAsync(
                    settings,
                    amount,
                    txnRef,
                    receipts,
                    fallbackMessage,
                    cancellationToken);
            }

            return new PaymentAuthorizationResult(false, null, fallbackMessage);
        }
        finally
        {
            SafeDisconnect(client, txnRef);
        }
    }

    private async Task<PaymentAuthorizationResult> TryCancelActiveTransactionAsync(
        ILinklyEftClient client,
        CardTerminalSettings settings,
        decimal amount,
        string txnRef,
        IReadOnlyList<string> capturedReceipts)
    {
        var receipts = new List<string>(capturedReceipts);
        var fallbackMessage = T("linkly.local.cancelOutcomeUnknown", "ANZ Linkly cancellation outcome could not be confirmed.");
        using var cancelCts = CreateTimeoutToken(settings.TerminalTimeout, CancellationToken.None);
        var cancelRequest = CreateCancelRequest();

        try
        {
            LogJson(
                "cancel",
                "sent",
                "request",
                settings.Environment,
                txnRef,
                request: cancelRequest);
            if (!await client.SendCancelRequestAsync().WaitAsync(cancelCts.Token))
            {
                LogJson(
                    "cancel",
                    "failed",
                    "response",
                    settings.Environment,
                    txnRef,
                    success: false,
                    reason: "send-cancel-failed",
                    request: cancelRequest);
                return await TryRecoverLastTransactionAsync(
                    settings,
                    amount,
                    txnRef,
                    receipts,
                    fallbackMessage,
                    CancellationToken.None);
            }

            var response = await ReadTransactionResponseAsync(client, receipts, cancelCts.Token, settings.Environment, txnRef);
            return ToAuthorizationResult(response, amount, txnRef, receipts);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionException)
        {
            LogJson(
                "cancel",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: ex is OperationCanceledException ? "timeout" : "connection-closed",
                request: cancelRequest,
                details: new
                {
                    exception = ex.GetType().Name,
                    ex.Message
                });
            return await TryRecoverLastTransactionAsync(
                settings,
                amount,
                txnRef,
                receipts,
                fallbackMessage,
                CancellationToken.None);
        }
        catch
        {
            LogJson(
                "cancel",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: "unexpected-error",
                request: cancelRequest);
            return await TryRecoverLastTransactionAsync(
                settings,
                amount,
                txnRef,
                receipts,
                fallbackMessage,
                CancellationToken.None);
        }
    }

    private async Task<PaymentAuthorizationResult> TryRecoverLastTransactionAsync(
        CardTerminalSettings settings,
        decimal amount,
        string txnRef,
        IReadOnlyList<string> capturedReceipts,
        string fallbackMessage,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new PaymentAuthorizationResult(false, null, T("linkly.local.cancelled", CancelledMessage));
        }

        using var timeoutCts = CreateTimeoutToken(settings.TerminalTimeout, cancellationToken);
        using var client = clientFactory.Create();
        var receipts = new List<string>(capturedReceipts);
        var connectRequest = CreateConnectRequest(settings.LinklyHost, settings.LinklyPort);
        try
        {
            LogJson(
                "connect",
                "start",
                "request",
                settings.Environment,
                txnRef,
                request: connectRequest,
                details: new
                {
                    recovery = true
                });
            var connected = await client.ConnectAsync(settings.LinklyHost, settings.LinklyPort, useSsl: false, useKeepAlive: false)
                .WaitAsync(timeoutCts.Token);
            LogJson(
                "connect",
                connected ? "succeeded" : "failed",
                "response",
                settings.Environment,
                txnRef,
                success: connected,
                request: connectRequest,
                response: new
                {
                    connected
                },
                details: new
                {
                    recovery = true
                });
            if (!connected)
            {
                return new PaymentAuthorizationResult(false, null, fallbackMessage);
            }

            var request = new EFTGetLastTransactionRequest(txnRef)
            {
                Application = TerminalApplication.EFTPOS,
                Merchant = Merchant
            };
            LogJson(
                "get-last-transaction",
                "sent",
                "request",
                settings.Environment,
                txnRef,
                request: request);
            if (!await client.WriteRequestAsync(request).WaitAsync(timeoutCts.Token))
            {
                LogJson(
                    "get-last-transaction",
                    "failed",
                    "response",
                    settings.Environment,
                    txnRef,
                    success: false,
                    reason: "write-request-failed",
                    request: request);
                return new PaymentAuthorizationResult(false, null, fallbackMessage);
            }

            while (true)
            {
                var response = await client.ReadResponseAsync(timeoutCts.Token);
                switch (response)
                {
                    case EFTReceiptResponse receipt:
                        CaptureReceipt(receipts, receipt);
                        LogJson(
                            "receipt",
                            "received",
                            "response",
                            settings.Environment,
                            txnRef,
                            response: receipt,
                            details: new
                            {
                                recovery = true
                            });
                        break;
                    case EFTGetLastTransactionResponse last:
                        LogJson(
                            "get-last-transaction",
                            "received",
                            "response",
                            settings.Environment,
                            txnRef,
                            success: last.Success && last.LastTransactionSuccess,
                            request: request,
                            response: last);
                        return ToAuthorizationResult(last, amount, txnRef, receipts);
                    case EFTTransactionResponse transaction:
                        LogJson(
                            "transaction",
                            "received",
                            "response",
                            settings.Environment,
                            txnRef,
                            success: transaction.Success,
                            request: request,
                            response: transaction,
                            details: new
                            {
                                recovery = true
                            });
                        return ToAuthorizationResult(transaction, amount, txnRef, receipts);
                    case null:
                        LogJson(
                            "get-last-transaction",
                            "failed",
                            "response",
                            settings.Environment,
                            txnRef,
                            success: false,
                            reason: "empty-response",
                            request: request);
                        return new PaymentAuthorizationResult(false, null, fallbackMessage);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogJson(
                "get-last-transaction",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: "caller-cancelled");
            return new PaymentAuthorizationResult(false, null, T("linkly.local.cancelled", CancelledMessage));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionException)
        {
            LogJson(
                "get-last-transaction",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: ex is OperationCanceledException ? "timeout" : "connection-closed",
                details: new
                {
                    exception = ex.GetType().Name,
                    ex.Message
                });
            return new PaymentAuthorizationResult(false, null, fallbackMessage);
        }
        catch
        {
            LogJson(
                "get-last-transaction",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: "unexpected-error");
            return new PaymentAuthorizationResult(false, null, fallbackMessage);
        }
        finally
        {
            SafeDisconnect(client, txnRef);
        }
    }

    private async Task<EFTTransactionResponse> ReadTransactionResponseAsync(
        ILinklyEftClient client,
        ICollection<string> receipts,
        CancellationToken cancellationToken,
        CardTerminalEnvironment environment,
        string txnRef)
    {
        while (true)
        {
            var response = await client.ReadResponseAsync(cancellationToken);
            switch (response)
            {
                case EFTReceiptResponse receipt:
                    CaptureReceipt(receipts, receipt);
                    LogJson(
                        "receipt",
                        "received",
                        "response",
                        environment,
                        txnRef,
                        response: receipt);
                    break;
                case EFTTransactionResponse transaction:
                    LogJson(
                        "transaction",
                        "received",
                        "response",
                        environment,
                        txnRef,
                        success: transaction.Success,
                        response: transaction);
                    return transaction;
                case null:
                    LogJson(
                        "transaction",
                        "failed",
                        "response",
                        environment,
                        txnRef,
                        success: false,
                        reason: "empty-response");
                    throw new InvalidOperationException(T("linkly.local.emptyResponse", "ANZ Linkly returned an empty response."));
            }
        }
    }

    private static EFTTransactionRequest CreateTransactionRequest(
        TransactionType transactionType,
        decimal amount,
        string txnRef)
    {
        return new EFTTransactionRequest
        {
            TxnType = transactionType,
            AmtPurchase = decimal.Round(amount, 2, MidpointRounding.AwayFromZero),
            AmtCash = 0m,
            TxnRef = txnRef,
            Application = TerminalApplication.EFTPOS,
            Merchant = Merchant,
            ReceiptAutoPrint = ReceiptPrintModeType.POSPrinter
        };
    }

    private PaymentAuthorizationResult ToAuthorizationResult(
        EFTTransactionResponse response,
        decimal requestedAmount,
        string requestedTxnRef,
        IReadOnlyList<string> receipts)
    {
        var amount = response.AmtPurchase > 0m ? response.AmtPurchase : requestedAmount;
        var txnRef = string.IsNullOrWhiteSpace(response.TxnRef) ? requestedTxnRef : response.TxnRef.Trim();
        var transaction = ToCardTransaction(response, amount, txnRef, receipts);
        return response.Success
            ? new PaymentAuthorizationResult(true, $"ANZ:{txnRef}", "ANZ Linkly", amount, [transaction])
            : new PaymentAuthorizationResult(false, $"ANZ:{txnRef}", FormatResponseMessage(response.ResponseText, response.ResponseCode), amount, [transaction]);
    }

    private PaymentAuthorizationResult ToAuthorizationResult(
        EFTGetLastTransactionResponse response,
        decimal requestedAmount,
        string requestedTxnRef,
        IReadOnlyList<string> receipts)
    {
        var amount = response.AmtPurchase > 0m ? response.AmtPurchase : requestedAmount;
        var txnRef = string.IsNullOrWhiteSpace(response.TxnRef) ? requestedTxnRef : response.TxnRef.Trim();
        var transaction = ToCardTransaction(response, amount, txnRef, receipts);
        return response.Success && response.LastTransactionSuccess
            ? new PaymentAuthorizationResult(true, $"ANZ:{txnRef}", "ANZ Linkly", amount, [transaction])
            : new PaymentAuthorizationResult(false, $"ANZ:{txnRef}", FormatResponseMessage(response.ResponseText, response.ResponseCode), amount, [transaction]);
    }

    private static CardTransactionDto ToCardTransaction(
        EFTTransactionResponse response,
        decimal amount,
        string txnRef,
        IReadOnlyList<string> receipts)
    {
        return new CardTransactionDto(
            ProcessorName,
            txnRef,
            FormatPositiveInt(response.AuthCode),
            NormalizeOptional(response.CardType),
            PositiveIntOrNull(response.CardName),
            MaskCardNumber(response.Pan),
            NormalizeOptional(response.Caid),
            NormalizeOptional(response.ResponseCode),
            NormalizeOptional(response.ResponseText),
            FormatPositiveInt(response.Stan),
            ToDateTimeOffsetOrNull(response.DateSettlement),
            decimal.Round(amount, 2, MidpointRounding.AwayFromZero),
            JoinReceipts(receipts));
    }

    private static CardTransactionDto ToCardTransaction(
        EFTGetLastTransactionResponse response,
        decimal amount,
        string txnRef,
        IReadOnlyList<string> receipts)
    {
        return new CardTransactionDto(
            ProcessorName,
            txnRef,
            FormatPositiveInt(response.AuthCode),
            NormalizeOptional(response.CardType),
            PositiveIntOrNull(response.CardName),
            MaskCardNumber(response.Pan),
            NormalizeOptional(response.Caid),
            NormalizeOptional(response.ResponseCode),
            NormalizeOptional(response.ResponseText),
            FormatPositiveInt(response.Stan),
            ToDateTimeOffsetOrNull(response.DateSettlement),
            decimal.Round(amount, 2, MidpointRounding.AwayFromZero),
            JoinReceipts(receipts));
    }

    private static void CaptureReceipt(ICollection<string> receipts, EFTReceiptResponse receipt)
    {
        if (receipt.ReceiptText is not { Length: > 0 } receiptText)
        {
            return;
        }

        receipts.Add(string.Join(Environment.NewLine, receiptText));
    }

    private static CancellationTokenSource CreateTimeoutToken(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(180) : timeout);
        return timeoutCts;
    }

    private static string BuildTxnRef(PosSessionState session)
    {
        var device = new string(session.DeviceCode.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(device))
        {
            device = "POS";
        }

        return Limit($"{device}{DateTimeOffset.UtcNow:yyMMddHHmmssfff}", 32);
    }

    private static string? NormalizeReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var trimmed = reference.Trim();
        return trimmed.StartsWith("ANZ:", StringComparison.OrdinalIgnoreCase)
            ? trimmed[4..].Trim()
            : trimmed;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? FormatPositiveInt(int value)
    {
        return value > 0 ? value.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static int? PositiveIntOrNull(int value)
    {
        return value > 0 ? value : null;
    }

    private static DateTimeOffset? ToDateTimeOffsetOrNull(DateTime value)
    {
        return value == default
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
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
        return digits.Length <= 4
            ? digits
            : $"****{digits[^4..]}";
    }

    private static string? JoinReceipts(IReadOnlyList<string> receipts)
    {
        return receipts.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, receipts);
    }

    private string FormatResponseMessage(string? responseText, string? responseCode)
    {
        var text = NormalizeOptional(responseText);
        var code = NormalizeOptional(responseCode);
        if (text is null && code is null)
        {
            return T("linkly.local.declined", "ANZ Linkly transaction was declined.");
        }

        return code is null ? text! : $"{text ?? T("linkly.local.declined", "ANZ Linkly transaction was declined.")} ({code})";
    }

    private string T(string key, string fallback)
    {
        var value = localization?.T(key);
        return string.IsNullOrWhiteSpace(value) || value == $"[[{key}]]" ? fallback : value;
    }

    private static string Limit(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private void LogJson(
        string operation,
        string phase,
        string? direction = null,
        CardTerminalEnvironment? environment = null,
        string? sessionId = null,
        bool? success = null,
        string? reason = null,
        object? request = null,
        object? response = null,
        object? details = null)
    {
        try
        {
            LinklyJsonLog.Write(
                LogCategory,
                LogSource,
                operation,
                phase,
                direction,
                environment,
                sessionId,
                success: success,
                reason: reason,
                request: NormalizeLogPayload(request),
                response: NormalizeLogPayload(response),
                details: details);
        }
        catch
        {
            // 日志仅用于诊断，不能影响刷卡主流程。
        }
    }

    private static object? NormalizeLogPayload(object? payload)
    {
        return payload switch
        {
            null => null,
            EFTTransactionRequest request => new
            {
                request.TxnType,
                request.AmtPurchase,
                request.AmtCash,
                request.TxnRef,
                request.Application,
                request.Merchant,
                request.ReceiptAutoPrint
            },
            EFTGetLastTransactionRequest request => new
            {
                request.TxnRef,
                request.Application,
                request.Merchant
            },
            EFTSendKeyRequest request => new
            {
                request.Key
            },
            EFTReceiptResponse response => new
            {
                response.ReceiptText
            },
            EFTTransactionResponse response => new
            {
                response.Success,
                response.TxnRef,
                response.AmtPurchase,
                response.ResponseCode,
                response.ResponseText,
                response.AuthCode,
                response.CardType,
                response.Pan,
                response.Caid,
                response.Stan,
                response.CardName,
                response.DateSettlement
            },
            EFTGetLastTransactionResponse response => new
            {
                response.Success,
                response.LastTransactionSuccess,
                response.TxnRef,
                response.AmtPurchase,
                response.ResponseCode,
                response.ResponseText,
                response.AuthCode,
                response.CardType,
                response.Pan,
                response.Caid,
                response.Stan,
                response.CardName,
                response.DateSettlement
            },
            _ => payload
        };
    }

    private static object CreateConnectRequest(string host, int port)
    {
        return new
        {
            host,
            port,
            useSsl = false,
            useKeepAlive = false
        };
    }

    private static EFTSendKeyRequest CreateCancelRequest()
    {
        return new EFTSendKeyRequest
        {
            Key = EFTPOSKey.OkCancel
        };
    }

    private void SafeDisconnect(ILinklyEftClient client, string? txnRef)
    {
        try
        {
            var disconnected = client.Disconnect();
            LogJson(
                "disconnect",
                disconnected ? "succeeded" : "failed",
                "response",
                sessionId: txnRef,
                success: disconnected,
                response: new
                {
                    disconnected
                });
        }
        catch (Exception ex)
        {
            LogJson(
                "disconnect",
                "failed",
                "response",
                sessionId: txnRef,
                success: false,
                reason: ex.GetType().Name,
                details: new
                {
                    ex.Message
                });
        }
    }
}
