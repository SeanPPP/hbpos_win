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
        Assert.Equal(CardTerminalEnvironment.Production, store.LastEnvironment);
        Assert.Equal("S01", store.LastStoreCode);
        Assert.Equal("TERM-1", store.LastDeviceCode);
        var transaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("ANZ", transaction.Processor);
        Assert.Equal("****1234", transaction.MaskedCardNumber);
        Assert.Equal("RFN-1", transaction.RefundReference);
    }

    [Fact]
    public async Task PurchaseAsync_closes_dialog_after_approved_direct_cloud_transaction()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            TransactionResult = Approved("session-1", "TXN-1")
        };
        var dialog = new FakeLinklyTerminalDialogService
        {
            ThrowIfFinalStateUsesCancelableToken = true
        };
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            localization: null,
            dialogService: dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(1, dialog.CloseCallCount);
        Assert.Contains(dialog.States, state => state.IsFinal);
    }

    [Fact]
    public async Task PurchaseAsync_keeps_dialog_open_after_declined_direct_cloud_transaction()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            TransactionResult = new LinklyCloudTransactionResult(
                "session-1",
                false,
                "TXN-1",
                null,
                null,
                null,
                null,
                null,
                "05",
                "DECLINED",
                null,
                10m,
                null)
        };
        var dialog = new FakeLinklyTerminalDialogService();
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            localization: null,
            dialogService: dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal(0, dialog.CloseCallCount);
        var finalState = Assert.Single(dialog.States.Where(state => state.IsFinal));
        Assert.Equal("DECLINED (05)", finalState.ResponseText);
    }

    [Fact]
    public async Task PurchaseAsync_returns_result_unknown_when_status_poll_fails_after_direct_submission()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionResultSequence.Enqueue(new LinklyCloudTransactionResult(
            "session-pending",
            false,
            "TXN-PENDING",
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
        });
        apiClient.TransactionStatusSequence.Enqueue(new HttpRequestException("status offline"));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.False(result.FallbackAllowed);
        Assert.Equal("linkly.cloud.resultUnknown", result.StatusKey);
        Assert.Equal(1, apiClient.SendTransactionCallCount);
        Assert.Equal(1, apiClient.GetTransactionCallCount);
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
    public async Task PurchaseAsync_rejects_mismatched_endpoint_before_token_request()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings() with
        {
            Environment = CardTerminalEnvironment.Production,
            LinklyCloudAuthBaseUrl = CardTerminalSettings.GetLinklyCloudAuthBaseUrl(CardTerminalEnvironment.Sandbox)
        });

        Assert.False(result.Approved);
        Assert.Equal(
            "Linkly Cloud Auth endpoint does not match the selected Production environment. Update the configured host and try again.",
            result.Message);
        Assert.Equal(0, apiClient.TokenCallCount);
    }

    [Fact]
    public async Task TestConnectionAsync_sends_logon_and_fails_when_logon_is_declined()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            LogonResult = new LinklyCloudLogonResult(
                false,
                "TF",
                "LOGON FAILED",
                "CAT-1",
                "CA-1",
                "1.0")
        };
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore());

        var result = await client.TestConnectionAsync(CreateSettings(), "S01", "TERM-1");

        Assert.False(result.Succeeded);
        Assert.Equal("LOGON FAILED (TF)", result.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_succeeds_when_logon_is_successful()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            LogonResult = new LinklyCloudLogonResult(
                true,
                "00",
                "APPROVED",
                "CAT-1",
                "CA-1",
                "1.0")
        };
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore());

        var result = await client.TestConnectionAsync(CreateSettings(), "S01", "TERM-1");

        Assert.True(result.Succeeded);
        Assert.Equal("APPROVED (00)", result.Message);
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

    [Fact]
    public async Task PurchaseAsync_sends_direct_cancel_sendkey_when_dialog_requests_cancel()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionResultSequence.Enqueue(Pending("session-cancel-1"));
        apiClient.TransactionStatusSequence.Enqueue(new LinklyCloudTransactionResult(
            "session-cancel-1",
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            "CN",
            "CANCELLED",
            null,
            10m,
            null));
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.OkCancel, null));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            localization: null,
            dialogService: dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("CANCELLED (CN)", result.Message);
        Assert.Equal(1, dialog.CloseCallCount);
        Assert.Equal(1, apiClient.SendKeyCallCount);
        Assert.Equal(1, apiClient.GetTransactionCallCount);
        Assert.Equal(apiClient.LastTransactionSessionId, apiClient.LastSendKeySessionId);
        Assert.Equal(LinklyTerminalDialogKeys.OkCancel, apiClient.LastSendKeyKey);
        Assert.Contains(dialog.States, state =>
            state.SessionId == apiClient.LastTransactionSessionId &&
            state.IsInteractive &&
            !state.IsFinal);
    }

    [Fact]
    public async Task PurchaseAsync_allows_direct_cancel_while_initial_transaction_request_is_pending()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            PendingTransactionCompletion = new TaskCompletionSource<LinklyCloudTransactionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.OkCancel, null));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            localization: null,
            dialogService: dialog);

        var purchaseTask = client.PurchaseAsync(10m, CreateSession(), CreateSettings());
        await WaitUntilAsync(() => apiClient.SendKeyCallCount == 1);

        Assert.Equal(apiClient.LastTransactionSessionId, apiClient.LastSendKeySessionId);
        Assert.Equal(LinklyTerminalDialogKeys.OkCancel, apiClient.LastSendKeyKey);
        var pendingState = Assert.Single(dialog.States.Where(state => state.IsInteractive && !state.IsFinal));
        Assert.Equal(apiClient.LastTransactionSessionId, pendingState.SessionId);

        apiClient.PendingTransactionCompletion.SetResult(Approved(apiClient.LastTransactionSessionId!, "TXN-4"));
        var result = await purchaseTask;

        Assert.True(result.Approved);
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

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private sealed class FakeLinklyCloudSecretStore : ILinklyCloudSecretStore
    {
        public string? LastStoreCode { get; private set; }

        public string? LastDeviceCode { get; private set; }

        public CardTerminalEnvironment? LastEnvironment { get; private set; }

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

        public Task<LinklyCloudCredentialSettings> GetLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyCloudCredentialSettings(null, null, false));
        }

        public Task SaveLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<string> GetOrCreateLinklyCloudPosIdAsync(
            CardTerminalEnvironment environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken = default)
        {
            LastEnvironment = environment;
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

        public string? LastTransactionSessionId { get; private set; }

        public TaskCompletionSource<LinklyCloudTransactionResult>? PendingTransactionCompletion { get; init; }

        public int GetTransactionCallCount { get; private set; }

        public int SendKeyCallCount { get; private set; }

        public string? LastSendKeySessionId { get; private set; }

        public string? LastSendKeyKey { get; private set; }

        public Queue<LinklyCloudTransactionResult> TransactionResultSequence { get; } = [];

        public Queue<object> TransactionStatusSequence { get; } = [];

        public LinklyCloudLogonResult LogonResult { get; init; } =
            new(true, "00", null, "CAT-1", "CA-1", "1.0");

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

        public Task<LinklyCloudLogonResult> SendLogonAsync(
            CardTerminalSettings settings,
            string token,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LogonResult);
        }

        public Task<LinklyCloudTransactionResult> SendTransactionAsync(
            CardTerminalSettings settings,
            string token,
            LinklyCloudTransactionRequest request,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            SendTransactionCallCount++;
            LastTransactionSessionId = sessionId;
            if (PendingTransactionCompletion is not null)
            {
                return PendingTransactionCompletion.Task;
            }

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

            if (next is Exception generalException)
            {
                throw generalException;
            }

            return Task.FromResult((LinklyCloudTransactionResult)next);
        }

        public Task SendKeyAsync(
            CardTerminalSettings settings,
            string token,
            string sessionId,
            string key,
            string? data,
            CancellationToken cancellationToken = default)
        {
            SendKeyCallCount++;
            LastSendKeySessionId = sessionId;
            LastSendKeyKey = LinklyTerminalDialogKeys.Normalize(key);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLinklyTerminalDialogService : ILinklyTerminalDialogService
    {
        public List<LinklyTerminalDialogState> States { get; } = [];

        public int CloseCallCount { get; private set; }

        public bool ThrowIfFinalStateUsesCancelableToken { get; init; }

        private readonly Queue<LinklyTerminalDialogAction?> _actions = new();

        public void EnqueueAction(LinklyTerminalDialogAction? action)
        {
            _actions.Enqueue(action);
        }

        public Task<LinklyTerminalDialogAction?> UpdateAsync(
            LinklyTerminalDialogState state,
            CancellationToken cancellationToken)
        {
            if (ThrowIfFinalStateUsesCancelableToken && state.IsFinal && cancellationToken.CanBeCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            States.Add(state);
            return Task.FromResult(state.IsInteractive && _actions.Count > 0 ? _actions.Dequeue() : null);
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            CloseCallCount++;
            return Task.CompletedTask;
        }
    }
}
