using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using PCEFTPOS.EFTClient.IPInterface;
using System.Text.Json;

namespace Hbpos.Client.Tests;

public sealed class LinklyTerminalClientTests
{
    [Fact]
    public async Task PurchaseAsync_sends_purchase_request_and_returns_card_transaction()
    {
        using var logs = new ConsoleLogCapture();
        var eftClient = new FakeLinklyEftClient(
            new EFTReceiptResponse
            {
                ReceiptText = ["MERCHANT COPY", "APPROVED"]
            },
            new EFTTransactionResponse
            {
                Success = true,
                TxnRef = "TXN-1",
                AmtPurchase = 10m,
                Pan = "4111111111111234",
                CardType = "VISA",
                AuthCode = 123456,
                CardName = 4,
                Caid = "MID-1",
                ResponseCode = "00",
                ResponseText = "APPROVED",
                Stan = 42,
                DateSettlement = DateTime.Parse("2026-05-26T00:00:00Z")
            });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal("ANZ:TXN-1", result.Reference);
        var request = Assert.IsType<EFTTransactionRequest>(eftClient.LastRequest);
        Assert.Equal(TransactionType.PurchaseCash, request.TxnType);
        Assert.Equal(10m, request.AmtPurchase);
        Assert.Equal("00", request.Merchant);
        Assert.Equal(TerminalApplication.EFTPOS, request.Application);
        var transaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("ANZ", transaction.Processor);
        Assert.Equal("TXN-1", transaction.TxnRef);
        Assert.Equal("****1234", transaction.MaskedCardNumber);
        Assert.Contains("MERCHANT COPY", transaction.ReceiptText);

        var events = logs.ReadJsonEvents("LinklyLocal");
        var connectEvent = AssertEvent(events, "connect", "succeeded", "response");
        Assert.True(connectEvent.GetProperty("response").GetProperty("connected").GetBoolean());
        var requestEvent = AssertEvent(events, "transaction", "sent", "request");
        Assert.True(requestEvent.TryGetProperty("request", out var requestJson));
        Assert.StartsWith("TERM1", requestJson.GetProperty("txnRef").GetString(), StringComparison.Ordinal);
        Assert.Equal("00", requestJson.GetProperty("merchant").GetString());
        Assert.Equal("10", requestJson.GetProperty("amtPurchase").GetRawText());
        var receiptEvent = AssertEvent(events, "receipt", "received", "response");
        Assert.True(receiptEvent.TryGetProperty("response", out var receiptJson));
        Assert.Equal("MERCHANT COPY", receiptJson.GetProperty("receiptText")[0].GetString());
        var responseEvent = AssertEvent(events, "transaction", "received", "response");
        Assert.True(responseEvent.TryGetProperty("response", out var responseJson));
        Assert.Equal("TXN-1", responseJson.GetProperty("txnRef").GetString());
        Assert.Equal("00", responseJson.GetProperty("responseCode").GetString());
        Assert.Equal("10", responseJson.GetProperty("amtPurchase").GetRawText());
        var disconnectEvent = AssertEvent(events, "disconnect", "succeeded", "response");
        Assert.True(disconnectEvent.GetProperty("response").GetProperty("disconnected").GetBoolean());
    }

    [Fact]
    public async Task PurchaseAsync_fails_closed_when_connection_fails()
    {
        using var logs = new ConsoleLogCapture();
        var eftClient = new FakeLinklyEftClient { ConnectResult = false };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Contains("connection failed", result.Message, StringComparison.OrdinalIgnoreCase);
        var events = logs.ReadJsonEvents("LinklyLocal");
        var connectEvent = AssertEvent(events, "connect", "failed", "response");
        Assert.True(connectEvent.TryGetProperty("request", out _));
        Assert.True(connectEvent.TryGetProperty("response", out var responseJson));
        Assert.False(responseJson.GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task PurchaseAsync_fails_closed_when_request_cannot_be_sent()
    {
        var eftClient = new FakeLinklyEftClient { WriteResult = false };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Contains("could not be sent", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PurchaseAsync_fails_closed_for_declined_response()
    {
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = false,
            TxnRef = "TXN-DECLINE",
            AmtPurchase = 10m,
            ResponseCode = "05",
            ResponseText = "DECLINED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("ANZ:TXN-DECLINE", result.Reference);
        Assert.Contains("DECLINED", result.Message);
    }

    [Fact]
    public async Task PurchaseAsync_recovers_approved_get_last_transaction_after_timeout()
    {
        var purchaseClient = new FakeLinklyEftClient { ThrowOnRead = true };
        var getLastClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = true,
            TxnRef = "TERM12605260000000",
            AmtPurchase = 10m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new QueueLinklyEftClientFactory(purchaseClient, getLastClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.StartsWith("ANZ:TERM1", result.Reference, StringComparison.Ordinal);
        Assert.IsType<EFTGetLastTransactionRequest>(getLastClient.LastRequest);
    }

    [Fact]
    public async Task PurchaseAsync_sends_cancel_key_after_caller_cancels_and_returns_decline()
    {
        using var cts = new CancellationTokenSource();
        var readCount = 0;
        var purchaseClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = false,
            TxnRef = "TERM12605260000000",
            AmtPurchase = 10m,
            ResponseCode = "C0",
            ResponseText = "CANCELLED"
        })
        {
            OnRead = () =>
            {
                if (readCount++ == 0)
                {
                    cts.Cancel();
                }
            }
        };
        purchaseClient.ReadExceptions.Enqueue(new OperationCanceledException(cts.Token));
        var factory = new QueueLinklyEftClientFactory(purchaseClient);
        var client = new LinklyTerminalClient(factory);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings(), cts.Token);

        Assert.False(result.Approved);
        Assert.Contains("CANCELLED", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, factory.CreatedCount);
        Assert.IsType<EFTTransactionRequest>(purchaseClient.Requests[0]);
        var cancelRequest = Assert.IsType<EFTSendKeyRequest>(purchaseClient.Requests[1]);
        Assert.Equal(EFTPOSKey.OkCancel, cancelRequest.Key);
    }

    [Fact]
    public async Task PurchaseAsync_recovers_approved_get_last_transaction_after_cancel_outcome_is_unknown()
    {
        using var logs = new ConsoleLogCapture();
        using var cts = new CancellationTokenSource();
        var readCount = 0;
        var purchaseClient = new FakeLinklyEftClient
        {
            CancelRequestResult = false,
            OnRead = () =>
            {
                if (readCount++ == 0)
                {
                    cts.Cancel();
                }
            }
        };
        purchaseClient.ReadExceptions.Enqueue(new OperationCanceledException(cts.Token));
        var getLastClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = true,
            TxnRef = "TERM12605260000000",
            AmtPurchase = 10m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new QueueLinklyEftClientFactory(purchaseClient, getLastClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings(), cts.Token);

        Assert.True(result.Approved);
        Assert.Equal("ANZ:TERM12605260000000", result.Reference);
        Assert.IsType<EFTSendKeyRequest>(purchaseClient.Requests[1]);
        Assert.IsType<EFTGetLastTransactionRequest>(getLastClient.LastRequest);
        var events = logs.ReadJsonEvents("LinklyLocal");
        var cancelRequestEvent = AssertEvent(events, "cancel", "sent", "request");
        Assert.True(cancelRequestEvent.GetProperty("request").TryGetProperty("key", out _));
        var cancelFailedEvent = AssertEvent(events, "cancel", "failed", "response");
        Assert.Equal("send-cancel-failed", cancelFailedEvent.GetProperty("reason").GetString());
        var recoveryRequestEvent = AssertEvent(events, "get-last-transaction", "sent", "request");
        Assert.StartsWith("TERM1", recoveryRequestEvent.GetProperty("request").GetProperty("txnRef").GetString(), StringComparison.Ordinal);
        var recoveryResponseEvent = AssertEvent(events, "get-last-transaction", "received", "response");
        Assert.True(recoveryResponseEvent.TryGetProperty("response", out var recoveryResponse));
        Assert.Equal("TERM12605260000000", recoveryResponse.GetProperty("txnRef").GetString());
        Assert.Equal("00", recoveryResponse.GetProperty("responseCode").GetString());
        Assert.True(recoveryResponseEvent.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task PurchaseAsync_recovers_approved_get_last_transaction_after_unknown_exception()
    {
        using var logs = new ConsoleLogCapture();
        var purchaseClient = new FakeLinklyEftClient();
        purchaseClient.ReadExceptions.Enqueue(new InvalidOperationException("Linkly parser failed."));
        var getLastClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = true,
            TxnRef = "TERM12605260000000",
            AmtPurchase = 10m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new QueueLinklyEftClientFactory(purchaseClient, getLastClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal("ANZ:TERM12605260000000", result.Reference);
        Assert.IsType<EFTGetLastTransactionRequest>(getLastClient.LastRequest);
        var events = logs.ReadJsonEvents("LinklyLocal");
        var failureEvent = AssertEvent(events, "transaction", "failed", "response");
        Assert.True(failureEvent.TryGetProperty("request", out var failedRequest));
        Assert.True(failedRequest.TryGetProperty("txnType", out _));
        Assert.Equal("InvalidOperationException", failureEvent.GetProperty("reason").GetString());
        Assert.Equal("Linkly parser failed.", failureEvent.GetProperty("details").GetProperty("message").GetString());
        var recoveryEvent = AssertEvent(events, "get-last-transaction", "received", "response");
        Assert.True(recoveryEvent.TryGetProperty("response", out var recoveryResponse));
        Assert.Equal("TERM12605260000000", recoveryResponse.GetProperty("txnRef").GetString());
    }

    [Fact]
    public async Task PurchaseAsync_returns_original_timeout_when_get_last_transaction_connect_throws()
    {
        var purchaseClient = new FakeLinklyEftClient { ThrowOnRead = true };
        var getLastClient = new FakeLinklyEftClient
        {
            ConnectException = new NullReferenceException("Third-party Linkly connect failed.")
        };
        var client = new LinklyTerminalClient(new QueueLinklyEftClientFactory(purchaseClient, getLastClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("ANZ Linkly transaction timed out.", result.Message);
    }

    [Fact]
    public async Task PurchaseAsync_fails_when_get_last_transaction_is_not_successful()
    {
        var purchaseClient = new FakeLinklyEftClient { ThrowOnRead = true };
        var getLastClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = false,
            TxnRef = "TERM12605260000000",
            AmtPurchase = 10m,
            ResponseCode = "05",
            ResponseText = "DECLINED"
        });
        var client = new LinklyTerminalClient(new QueueLinklyEftClientFactory(purchaseClient, getLastClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Contains("DECLINED", result.Message);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState(
            "HB POS",
            "1001",
            "Main",
            "TERM-1",
            "C001",
            "Cashier",
            true,
            0);
    }

    private static CardTerminalSettings CreateSettings()
    {
        return new CardTerminalSettings(
            CardProcessorKind.Linkly,
            CardTerminalEnvironment.Production,
            "127.0.0.1",
            2011,
            null,
            null,
            null,
            CardTerminalSettings.GetSquareApiBaseUrl(CardTerminalEnvironment.Production),
            TimeSpan.FromSeconds(10));
    }

    private sealed class FakeLinklyEftClientFactory(ILinklyEftClient client) : ILinklyEftClientFactory
    {
        public ILinklyEftClient Create()
        {
            return client;
        }
    }

    private sealed class QueueLinklyEftClientFactory(params ILinklyEftClient[] clients) : ILinklyEftClientFactory
    {
        private readonly Queue<ILinklyEftClient> _clients = new(clients);

        public int CreatedCount { get; private set; }

        public ILinklyEftClient Create()
        {
            CreatedCount++;
            return _clients.Dequeue();
        }
    }

    private sealed class FakeLinklyEftClient(params EFTResponse[] responses) : ILinklyEftClient
    {
        private readonly Queue<EFTResponse> _responses = new(responses);

        public EFTRequest? LastRequest { get; private set; }

        public List<EFTRequest> Requests { get; } = [];

        public Queue<Exception> ReadExceptions { get; } = new();

        public bool ConnectResult { get; init; } = true;

        public bool WriteResult { get; init; } = true;

        public bool CancelRequestResult { get; init; } = true;

        public bool ThrowOnRead { get; init; }

        public Exception? ConnectException { get; init; }

        public Action? OnRead { get; init; }

        public Task<bool> ConnectAsync(string hostName, int hostPort, bool useSsl, bool useKeepAlive)
        {
            if (ConnectException is not null)
            {
                throw ConnectException;
            }

            return Task.FromResult(ConnectResult);
        }

        public Task<bool> WriteRequestAsync(EFTRequest request)
        {
            LastRequest = request;
            Requests.Add(request);
            return Task.FromResult(WriteResult);
        }

        public Task<bool> SendCancelRequestAsync()
        {
            var request = new EFTSendKeyRequest { Key = EFTPOSKey.OkCancel };
            LastRequest = request;
            Requests.Add(request);
            return Task.FromResult(CancelRequestResult);
        }

        public Task<EFTResponse?> ReadResponseAsync(CancellationToken cancellationToken)
        {
            OnRead?.Invoke();
            if (ReadExceptions.Count > 0)
            {
                throw ReadExceptions.Dequeue();
            }

            if (ThrowOnRead)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return Task.FromResult<EFTResponse?>(_responses.Dequeue());
        }

        public bool Disconnect()
        {
            return true;
        }

        public void Dispose()
        {
        }
    }

    private static JsonElement AssertEvent(
        IReadOnlyList<JsonElement> events,
        string operation,
        string phase,
        string direction)
    {
        var match = events.FirstOrDefault(element =>
            string.Equals(element.GetProperty("operation").GetString(), operation, StringComparison.Ordinal) &&
            string.Equals(element.GetProperty("phase").GetString(), phase, StringComparison.Ordinal) &&
            string.Equals(element.GetProperty("direction").GetString(), direction, StringComparison.Ordinal));
        Assert.True(match.ValueKind != JsonValueKind.Undefined, $"Missing log event {operation}/{phase}/{direction}.");
        return match;
    }

    private static JsonElement ParseJsonPayload(string line)
    {
        var jsonStart = line.IndexOf('{', StringComparison.Ordinal);
        Assert.True(jsonStart >= 0, $"Expected JSON payload in line: {line}");
        using var document = JsonDocument.Parse(line[jsonStart..]);
        return document.RootElement.Clone();
    }

    private sealed class ConsoleLogCapture : IDisposable
    {
        private readonly List<string> _lines = [];

        public ConsoleLogCapture()
        {
            ConsoleLog.LineWritten += OnLineWritten;
        }

        public void Dispose()
        {
            ConsoleLog.LineWritten -= OnLineWritten;
        }

        public IReadOnlyList<JsonElement> ReadJsonEvents(string category)
        {
            lock (_lines)
            {
                return _lines
                    .Where(line => line.Contains($"[HBPOS][Client][{category}]", StringComparison.Ordinal))
                    .Select(ParseJsonPayload)
                    .ToArray();
            }
        }

        private void OnLineWritten(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }
}
