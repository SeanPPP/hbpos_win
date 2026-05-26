using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using PCEFTPOS.EFTClient.IPInterface;

namespace Hbpos.Client.Tests;

public sealed class LinklyTerminalClientTests
{
    [Fact]
    public async Task PurchaseAsync_sends_purchase_request_and_returns_card_transaction()
    {
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
    }

    [Fact]
    public async Task PurchaseAsync_fails_closed_when_connection_fails()
    {
        var eftClient = new FakeLinklyEftClient { ConnectResult = false };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Contains("connection failed", result.Message, StringComparison.OrdinalIgnoreCase);
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

        public ILinklyEftClient Create()
        {
            return _clients.Dequeue();
        }
    }

    private sealed class FakeLinklyEftClient(params EFTResponse[] responses) : ILinklyEftClient
    {
        private readonly Queue<EFTResponse> _responses = new(responses);

        public EFTRequest? LastRequest { get; private set; }

        public bool ConnectResult { get; init; } = true;

        public bool WriteResult { get; init; } = true;

        public bool ThrowOnRead { get; init; }

        public Task<bool> ConnectAsync(string hostName, int hostPort, bool useSsl, bool useKeepAlive)
        {
            return Task.FromResult(ConnectResult);
        }

        public Task<bool> WriteRequestAsync(EFTRequest request)
        {
            LastRequest = request;
            return Task.FromResult(WriteResult);
        }

        public Task<EFTResponse?> ReadResponseAsync(CancellationToken cancellationToken)
        {
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
}
