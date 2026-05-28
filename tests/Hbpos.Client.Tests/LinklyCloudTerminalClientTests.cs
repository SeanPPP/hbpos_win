using System.Net;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class LinklyCloudTerminalClientTests
{
    [Fact]
    public async Task PurchaseAsync_uses_pos_id_and_maps_approved_transaction()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            TransactionResult = new LinklyCloudTransactionResult(
                "session-1",
                true,
                "TXN-1",
                "123456",
                "VISA",
                "4",
                "4111111111111234",
                "MID",
                "00",
                "APPROVED",
                "42",
                10m,
                "RFN-1")
        };
        var store = new FakeLinklyCloudSecretStore();
        var client = new LinklyCloudTerminalClient(apiClient, store);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal("ANZCLOUD:TXN-1:RFN-1", result.Reference);
        Assert.Equal("pos-id-1", apiClient.LastPosId);
        Assert.Equal("S01", store.LastStoreCode);
        Assert.Equal("TERM-1", store.LastDeviceCode);
        var transaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("ANZ", transaction.Processor);
        Assert.Equal("****1234", transaction.MaskedCardNumber);
    }

    [Fact]
    public async Task RefundAsync_requires_original_rfn_reference()
    {
        var client = new LinklyCloudTerminalClient(
            new FakeLinklyCloudApiClient(),
            new FakeLinklyCloudSecretStore());

        var result = await client.RefundAsync(5m, CreateSession(), CreateSettings(), "ANZ:LOCAL-REF");

        Assert.False(result.Approved);
        Assert.Equal("Linkly Cloud refund requires an original RFN reference.", result.Message);
    }

    [Fact]
    public async Task PurchaseAsync_returns_clear_failure_when_token_request_is_unauthorized()
    {
        var client = new LinklyCloudTerminalClient(
            new FakeLinklyCloudApiClient
            {
                TokenException = new LinklyCloudApiException(
                    "Linkly Cloud token request failed with HTTP 401.",
                    HttpStatusCode.Unauthorized)
            },
            new FakeLinklyCloudSecretStore());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("Linkly Cloud pairing is invalid. Pair the terminal again.", result.Message);
    }

    [Fact]
    public async Task PurchaseAsync_refreshes_token_when_recovery_status_is_unauthorized()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionResultSequence.Enqueue(Pending("session-1"));
        apiClient.TransactionStatusSequence.Enqueue(new LinklyCloudApiException(
            "Linkly Cloud transaction status request failed with HTTP 401.",
            HttpStatusCode.Unauthorized));
        apiClient.TransactionStatusSequence.Enqueue(Approved("session-1", "TXN-2"));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(2, apiClient.TokenCallCount);
        Assert.Equal(2, apiClient.GetTransactionCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_retries_once_when_recovery_status_reports_not_submitted()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionResultSequence.Enqueue(Pending("session-1"));
        apiClient.TransactionStatusSequence.Enqueue(new LinklyCloudTransactionResult(
            "session-1",
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null)
        {
            Outcome = LinklyCloudTransactionOutcome.NotSubmitted
        });
        apiClient.TransactionResultSequence.Enqueue(Approved("session-2", "TXN-3"));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(2, apiClient.SendTransactionCallCount);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState(
            "HB POS",
            "S01",
            "Main",
            "TERM-1",
            "C001",
            "Cashier",
            true,
            0);
    }

    private static CardTerminalSettings CreateSettings()
    {
        return CardTerminalSettings.FromEnvironment() with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyConnectionMode = LinklyConnectionMode.Cloud,
            LinklyCloudSecret = "paired-secret",
            LinklyPosVendorId = "a256b7ec-709d-4c7d-8ffe-57cc7ca1fd22",
            TerminalTimeout = TimeSpan.FromSeconds(5)
        };
    }

    private static LinklyCloudTransactionResult Pending(string sessionId)
    {
        return new LinklyCloudTransactionResult(
            sessionId,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null)
        {
            Outcome = LinklyCloudTransactionOutcome.Pending
        };
    }

    private static LinklyCloudTransactionResult Approved(string sessionId, string txnRef)
    {
        return new LinklyCloudTransactionResult(
            sessionId,
            true,
            txnRef,
            "123456",
            "VISA",
            "4",
            "4111111111111234",
            "MID",
            "00",
            "APPROVED",
            "42",
            10m,
            "RFN-1");
    }

    private sealed class FakeLinklyCloudSecretStore : ILinklyCloudSecretStore
    {
        public string? LastStoreCode { get; private set; }

        public string? LastDeviceCode { get; private set; }

        public Task<string?> GetLinklyCloudSecretAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>("paired-secret");
        }

        public Task SaveLinklyCloudSecretAsync(
            CardTerminalEnvironment environment,
            string secret,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<string> GetOrCreateLinklyCloudPosIdAsync(
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken = default)
        {
            LastStoreCode = storeCode;
            LastDeviceCode = deviceCode;
            return Task.FromResult("pos-id-1");
        }
    }

    private sealed class FakeLinklyCloudApiClient : ILinklyCloudApiClient
    {
        public string? LastPosId { get; private set; }

        public LinklyCloudApiException? TokenException { get; init; }

        public int TokenCallCount { get; private set; }

        public int SendTransactionCallCount { get; private set; }

        public int GetTransactionCallCount { get; private set; }

        public Queue<LinklyCloudTransactionResult> TransactionResultSequence { get; } = [];

        public Queue<object> TransactionStatusSequence { get; } = [];

        public LinklyCloudTransactionResult TransactionResult { get; init; } =
            new("session-1", false, null, null, null, null, null, null, "05", "DECLINED", null, null, null);

        public Task<string> PairAsync(
            string authBaseUrl,
            string username,
            string password,
            string pairCode,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudToken> GetTokenAsync(
            CardTerminalSettings settings,
            string posId,
            CancellationToken cancellationToken = default)
        {
            TokenCallCount++;
            if (TokenException is not null)
            {
                throw TokenException;
            }

            LastPosId = posId;
            return Task.FromResult(new LinklyCloudToken("token", DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public Task<LinklyCloudStatusResult> SendStatusAsync(
            CardTerminalSettings settings,
            string token,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudTransactionResult> SendTransactionAsync(
            CardTerminalSettings settings,
            string token,
            LinklyCloudTransactionRequest request,
            CancellationToken cancellationToken = default)
        {
            SendTransactionCallCount++;
            return Task.FromResult(TransactionResultSequence.Count > 0
                ? TransactionResultSequence.Dequeue()
                : TransactionResult);
        }

        public Task<LinklyCloudTransactionResult> GetTransactionAsync(
            CardTerminalSettings settings,
            string token,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            GetTransactionCallCount++;
            if (TransactionStatusSequence.Count == 0)
            {
                throw new NotSupportedException();
            }

            var next = TransactionStatusSequence.Dequeue();
            if (next is LinklyCloudApiException exception)
            {
                throw exception;
            }

            return Task.FromResult((LinklyCloudTransactionResult)next);
        }
    }
}
