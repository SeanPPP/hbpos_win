using System.Globalization;
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

public sealed class LinklyTerminalClient(ILinklyEftClientFactory clientFactory) : ILinklyTerminalClient
{
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
            var connected = await client.ConnectAsync(host, port, useSsl: false, useKeepAlive: false)
                .WaitAsync(timeoutCts.Token);
            return connected
                ? new LinklyConnectionTestResult(true, "Linkly EFT-Client connection succeeded.")
                : new LinklyConnectionTestResult(false, "Linkly EFT-Client connection failed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LinklyConnectionTestResult(false, "Linkly EFT-Client connection timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new LinklyConnectionTestResult(false, $"Linkly connection failed: {ex.Message}");
        }
        finally
        {
            SafeDisconnect(client);
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
            return new PaymentAuthorizationResult(false, null, "Card amount must be greater than zero.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new PaymentAuthorizationResult(false, null, CancelledMessage);
        }

        using var timeoutCts = CreateTimeoutToken(settings.TerminalTimeout, cancellationToken);
        var txnRef = NormalizeReference(originalReference) ?? BuildTxnRef(session);
        var request = CreateTransactionRequest(transactionType, amount, txnRef);
        var receipts = new List<string>();
        using var client = clientFactory.Create();
        var transactionRequestSent = false;

        try
        {
            var connected = await client.ConnectAsync(settings.LinklyHost, settings.LinklyPort, useSsl: false, useKeepAlive: false)
                .WaitAsync(timeoutCts.Token);
            if (!connected)
            {
                return new PaymentAuthorizationResult(false, null, "ANZ Linkly EFT-Client connection failed.");
            }

            if (!await client.WriteRequestAsync(request).WaitAsync(timeoutCts.Token))
            {
                return new PaymentAuthorizationResult(false, null, "ANZ Linkly transaction request could not be sent.");
            }

            transactionRequestSent = true;
            var response = await ReadTransactionResponseAsync(client, receipts, timeoutCts.Token);
            return ToAuthorizationResult(response, amount, txnRef, receipts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (transactionRequestSent)
            {
                return await TryCancelActiveTransactionAsync(
                    client,
                    settings,
                    amount,
                    txnRef,
                    receipts);
            }

            return new PaymentAuthorizationResult(false, null, CancelledMessage);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionException)
        {
            var fallbackMessage = ex is OperationCanceledException
                ? "ANZ Linkly transaction timed out."
                : "ANZ Linkly connection was closed.";

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
            var fallbackMessage = $"ANZ Linkly transaction failed: {ex.Message}";
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
            SafeDisconnect(client);
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
        const string fallbackMessage = "ANZ Linkly cancellation outcome could not be confirmed.";
        using var cancelCts = CreateTimeoutToken(settings.TerminalTimeout, CancellationToken.None);

        try
        {
            if (!await client.SendCancelRequestAsync().WaitAsync(cancelCts.Token))
            {
                return await TryRecoverLastTransactionAsync(
                    settings,
                    amount,
                    txnRef,
                    receipts,
                    fallbackMessage,
                    CancellationToken.None);
            }

            var response = await ReadTransactionResponseAsync(client, receipts, cancelCts.Token);
            return ToAuthorizationResult(response, amount, txnRef, receipts);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionException)
        {
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
            return new PaymentAuthorizationResult(false, null, CancelledMessage);
        }

        using var timeoutCts = CreateTimeoutToken(settings.TerminalTimeout, cancellationToken);
        using var client = clientFactory.Create();
        var receipts = new List<string>(capturedReceipts);
        try
        {
            var connected = await client.ConnectAsync(settings.LinklyHost, settings.LinklyPort, useSsl: false, useKeepAlive: false)
                .WaitAsync(timeoutCts.Token);
            if (!connected)
            {
                return new PaymentAuthorizationResult(false, null, fallbackMessage);
            }

            var request = new EFTGetLastTransactionRequest(txnRef)
            {
                Application = TerminalApplication.EFTPOS,
                Merchant = Merchant
            };
            if (!await client.WriteRequestAsync(request).WaitAsync(timeoutCts.Token))
            {
                return new PaymentAuthorizationResult(false, null, fallbackMessage);
            }

            while (true)
            {
                var response = await client.ReadResponseAsync(timeoutCts.Token);
                switch (response)
                {
                    case EFTReceiptResponse receipt:
                        CaptureReceipt(receipts, receipt);
                        break;
                    case EFTGetLastTransactionResponse last:
                        return ToAuthorizationResult(last, amount, txnRef, receipts);
                    case EFTTransactionResponse transaction:
                        return ToAuthorizationResult(transaction, amount, txnRef, receipts);
                    case null:
                        return new PaymentAuthorizationResult(false, null, fallbackMessage);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PaymentAuthorizationResult(false, null, CancelledMessage);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionException)
        {
            return new PaymentAuthorizationResult(false, null, fallbackMessage);
        }
        catch
        {
            return new PaymentAuthorizationResult(false, null, fallbackMessage);
        }
        finally
        {
            SafeDisconnect(client);
        }
    }

    private static async Task<EFTTransactionResponse> ReadTransactionResponseAsync(
        ILinklyEftClient client,
        ICollection<string> receipts,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var response = await client.ReadResponseAsync(cancellationToken);
            switch (response)
            {
                case EFTReceiptResponse receipt:
                    CaptureReceipt(receipts, receipt);
                    break;
                case EFTTransactionResponse transaction:
                    return transaction;
                case null:
                    throw new InvalidOperationException("ANZ Linkly returned an empty response.");
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

    private static PaymentAuthorizationResult ToAuthorizationResult(
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

    private static PaymentAuthorizationResult ToAuthorizationResult(
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

    private static string FormatResponseMessage(string? responseText, string? responseCode)
    {
        var text = NormalizeOptional(responseText);
        var code = NormalizeOptional(responseCode);
        if (text is null && code is null)
        {
            return "ANZ Linkly transaction was declined.";
        }

        return code is null ? text! : $"{text ?? "ANZ Linkly transaction was declined."} ({code})";
    }

    private static string Limit(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static void SafeDisconnect(ILinklyEftClient client)
    {
        try
        {
            client.Disconnect();
        }
        catch
        {
        }
    }
}
