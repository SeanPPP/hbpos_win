using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using PCEFTPOS.EFTClient.IPInterface;

namespace Hbpos.Client.Tests;

public sealed class ConfiguredLinklyTerminalClientTests
{
    [Fact]
    public async Task PurchaseAsync_routes_local_ip_mode_to_local_linkly_client()
    {
        var localFactory = new FakeLinklyEftClientFactory();
        var client = new ConfiguredLinklyTerminalClient(
            new LinklyTerminalClient(localFactory),
            new FakeCloudTerminalClient(),
            new FakeBackendTerminalClient());

        var result = await client.PurchaseAsync(
            10m,
            CreateSession(),
            CreateSettings(LinklyConnectionMode.LocalIp));

        Assert.True(result.Approved);
        Assert.Equal("ANZ:LOCAL-1", result.Reference);
        Assert.Equal(1, localFactory.Client.ConnectCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_routes_cloud_direct_sync_mode_to_direct_cloud_client()
    {
        var cloud = new FakeCloudTerminalClient(new PaymentAuthorizationResult(true, "ANZCLOUD:DIRECT-1", AuthorizedAmount: 10m));
        var backend = new FakeBackendTerminalClient();
        var client = new ConfiguredLinklyTerminalClient(
            new LinklyTerminalClient(new FakeLinklyEftClientFactory()),
            cloud,
            backend);

        var result = await client.PurchaseAsync(
            10m,
            CreateSession(),
            CreateSettings(LinklyConnectionMode.CloudDirectSync));

        Assert.True(result.Approved);
        Assert.Equal("ANZCLOUD:DIRECT-1", result.Reference);
        Assert.Equal(1, cloud.PurchaseCallCount);
        Assert.Equal(0, backend.PurchaseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_routes_cloud_backend_async_mode_to_backend_client()
    {
        var cloud = new FakeCloudTerminalClient();
        var backend = new FakeBackendTerminalClient(new PaymentAuthorizationResult(true, "ANZBACKEND:ASYNC-1", AuthorizedAmount: 10m));
        var client = new ConfiguredLinklyTerminalClient(
            new LinklyTerminalClient(new FakeLinklyEftClientFactory()),
            cloud,
            backend);

        var result = await client.PurchaseAsync(
            10m,
            CreateSession(),
            CreateSettings(LinklyConnectionMode.CloudBackendAsync));

        Assert.True(result.Approved);
        Assert.Equal("ANZBACKEND:ASYNC-1", result.Reference);
        Assert.Equal(0, cloud.PurchaseCallCount);
        Assert.Equal(1, backend.PurchaseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_falls_back_to_next_mode_when_backend_communication_fails()
    {
        var cloud = new FakeCloudTerminalClient(new PaymentAuthorizationResult(
            true,
            "ANZCLOUD:DIRECT-1",
            AuthorizedAmount: 10m,
            ConnectionMode: LinklyConnectionMode.CloudDirectSync.ToString()));
        var backend = new FakeBackendTerminalClient(new PaymentAuthorizationResult(
            false,
            null,
            "ANZ Linkly Cloud backend communication failed.",
            StatusKey: "linkly.backend.communicationFailed",
            FallbackAllowed: true));
        var client = new ConfiguredLinklyTerminalClient(
            new LinklyTerminalClient(new FakeLinklyEftClientFactory()),
            cloud,
            backend);

        var result = await client.PurchaseAsync(
            10m,
            CreateSession(),
            CreateSettings(
                LinklyConnectionMode.CloudBackendAsync,
                [
                    LinklyConnectionMode.CloudBackendAsync,
                    LinklyConnectionMode.CloudDirectSync,
                    LinklyConnectionMode.LocalIp
                ]));

        Assert.True(result.Approved);
        Assert.Equal("ANZCLOUD:DIRECT-1", result.Reference);
        Assert.Equal(1, backend.PurchaseCallCount);
        Assert.Equal(1, cloud.PurchaseCallCount);
        Assert.True(result.FallbackSucceeded);
        Assert.Equal(LinklyConnectionMode.CloudBackendAsync.ToString(), result.RequestedConnectionMode);
        Assert.Equal(LinklyConnectionMode.CloudDirectSync.ToString(), result.ActualConnectionMode);
        Assert.Equal(
            [
                LinklyConnectionMode.CloudBackendAsync.ToString(),
                LinklyConnectionMode.CloudDirectSync.ToString()
            ],
            result.FallbackAttemptedModes);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_fallback_when_backend_result_is_unknown_after_submission()
    {
        var cloud = new FakeCloudTerminalClient(new PaymentAuthorizationResult(true, "ANZCLOUD:DIRECT-1", AuthorizedAmount: 10m));
        var backend = new FakeBackendTerminalClient(new PaymentAuthorizationResult(
            false,
            null,
            "ANZ Linkly Cloud backend communication failed after the transaction may have been submitted. Confirm the Linkly transaction status before retrying.",
            StatusKey: "linkly.backend.resultUnknown",
            ResultUnknown: true));
        var client = new ConfiguredLinklyTerminalClient(
            new LinklyTerminalClient(new FakeLinklyEftClientFactory()),
            cloud,
            backend);

        var result = await client.PurchaseAsync(
            10m,
            CreateSession(),
            CreateSettings(
                LinklyConnectionMode.CloudBackendAsync,
                [
                    LinklyConnectionMode.CloudBackendAsync,
                    LinklyConnectionMode.CloudDirectSync,
                    LinklyConnectionMode.LocalIp
                ]));

        Assert.False(result.Approved);
        Assert.Equal("linkly.backend.resultUnknown", result.StatusKey);
        Assert.True(result.ResultUnknown);
        Assert.Equal(1, backend.PurchaseCallCount);
        Assert.Equal(0, cloud.PurchaseCallCount);
        Assert.False(result.FallbackSucceeded);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_fallback_when_cloud_direct_result_is_unknown_after_submission()
    {
        var cloud = new FakeCloudTerminalClient(new PaymentAuthorizationResult(
            false,
            null,
            "Linkly Cloud communication failed after the transaction may have been submitted. Confirm the Linkly transaction status before retrying.",
            StatusKey: "linkly.cloud.resultUnknown",
            ResultUnknown: true));
        var backend = new FakeBackendTerminalClient(new PaymentAuthorizationResult(true, "ANZBACKEND:ASYNC-1", AuthorizedAmount: 10m));
        var client = new ConfiguredLinklyTerminalClient(
            new LinklyTerminalClient(new FakeLinklyEftClientFactory()),
            cloud,
            backend);

        var result = await client.PurchaseAsync(
            10m,
            CreateSession(),
            CreateSettings(
                LinklyConnectionMode.CloudDirectSync,
                [
                    LinklyConnectionMode.CloudDirectSync,
                    LinklyConnectionMode.CloudBackendAsync,
                    LinklyConnectionMode.LocalIp
                ]));

        Assert.False(result.Approved);
        Assert.Equal("linkly.cloud.resultUnknown", result.StatusKey);
        Assert.True(result.ResultUnknown);
        Assert.Equal(1, cloud.PurchaseCallCount);
        Assert.Equal(0, backend.PurchaseCallCount);
        Assert.False(result.FallbackSucceeded);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_fallback_when_backend_reports_active_session_recovery()
    {
        var cloud = new FakeCloudTerminalClient(new PaymentAuthorizationResult(true, "ANZCLOUD:DIRECT-1", AuthorizedAmount: 10m));
        var backend = new FakeBackendTerminalClient(new PaymentAuthorizationResult(
            false,
            null,
            "Current terminal already has an unfinished card transaction.",
            StatusKey: "linkly.backend.activeSessionRequiresRecovery"));
        var client = new ConfiguredLinklyTerminalClient(
            new LinklyTerminalClient(new FakeLinklyEftClientFactory()),
            cloud,
            backend);

        var result = await client.PurchaseAsync(
            10m,
            CreateSession(),
            CreateSettings(
                LinklyConnectionMode.CloudBackendAsync,
                [
                    LinklyConnectionMode.CloudBackendAsync,
                    LinklyConnectionMode.CloudDirectSync,
                    LinklyConnectionMode.LocalIp
                ]));

        Assert.False(result.Approved);
        Assert.Equal("linkly.backend.activeSessionRequiresRecovery", result.StatusKey);
        Assert.Equal(1, backend.PurchaseCallCount);
        Assert.Equal(0, cloud.PurchaseCallCount);
        Assert.False(result.FallbackSucceeded);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_fallback_after_timeout_because_result_may_be_unknown()
    {
        var cloud = new FakeCloudTerminalClient(new PaymentAuthorizationResult(
            false,
            null,
            "Linkly Cloud transaction timed out.",
            StatusKey: "linkly.cloud.timeout"));
        var backend = new FakeBackendTerminalClient(new PaymentAuthorizationResult(true, "ANZBACKEND:ASYNC-1", AuthorizedAmount: 10m));
        var client = new ConfiguredLinklyTerminalClient(
            new LinklyTerminalClient(new FakeLinklyEftClientFactory()),
            cloud,
            backend);

        var result = await client.PurchaseAsync(
            10m,
            CreateSession(),
            CreateSettings(
                LinklyConnectionMode.CloudDirectSync,
                [
                    LinklyConnectionMode.CloudDirectSync,
                    LinklyConnectionMode.CloudBackendAsync,
                    LinklyConnectionMode.LocalIp
                ]));

        Assert.False(result.Approved);
        Assert.Equal("linkly.cloud.timeout", result.StatusKey);
        Assert.Equal(1, cloud.PurchaseCallCount);
        Assert.Equal(0, backend.PurchaseCallCount);
        Assert.False(result.FallbackSucceeded);
    }

    [Fact]
    public async Task PurchaseAsync_returns_summary_when_all_priority_modes_have_communication_failures()
    {
        var localFactory = new FakeLinklyEftClientFactory(connectResult: false);
        var cloud = new FakeCloudTerminalClient(new PaymentAuthorizationResult(
            false,
            null,
            "Linkly Cloud communication failed.",
            StatusKey: "linkly.cloud.communicationFailed",
            FallbackAllowed: true));
        var backend = new FakeBackendTerminalClient(new PaymentAuthorizationResult(
            false,
            null,
            "ANZ Linkly Cloud backend communication failed.",
            StatusKey: "linkly.backend.communicationFailed",
            FallbackAllowed: true));
        var client = new ConfiguredLinklyTerminalClient(
            new LinklyTerminalClient(localFactory),
            cloud,
            backend);

        var result = await client.PurchaseAsync(
            10m,
            CreateSession(),
            CreateSettings(
                LinklyConnectionMode.CloudBackendAsync,
                [
                    LinklyConnectionMode.CloudBackendAsync,
                    LinklyConnectionMode.CloudDirectSync,
                    LinklyConnectionMode.LocalIp
                ]));

        Assert.False(result.Approved);
        Assert.Equal("payment.linklyFallback.allFailed", result.StatusKey);
        Assert.Contains("CloudBackendAsync", result.Message, StringComparison.Ordinal);
        Assert.Contains("CloudDirectSync", result.Message, StringComparison.Ordinal);
        Assert.Contains("LocalIp", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, backend.PurchaseCallCount);
        Assert.Equal(1, cloud.PurchaseCallCount);
        Assert.Equal(1, localFactory.Client.ConnectCallCount);
        Assert.Equal(
            [
                LinklyConnectionMode.CloudBackendAsync.ToString(),
                LinklyConnectionMode.CloudDirectSync.ToString(),
                LinklyConnectionMode.LocalIp.ToString()
            ],
            result.FallbackAttemptedModes);
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

    private static CardTerminalSettings CreateSettings(
        LinklyConnectionMode mode,
        IReadOnlyList<LinklyConnectionMode>? priority = null)
    {
        return CardTerminalSettings.FromEnvironment() with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyConnectionMode = mode,
            LinklyConnectionModePriority = priority ?? [mode],
            TerminalTimeout = TimeSpan.FromSeconds(5)
        };
    }

    private sealed class FakeCloudTerminalClient(
        PaymentAuthorizationResult? result = null) : ILinklyCloudTerminalClient
    {
        public int PurchaseCallCount { get; private set; }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            CardTerminalSettings settings,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(true));
        }

        public Task<PaymentAuthorizationResult> PurchaseAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default)
        {
            PurchaseCallCount++;
            return Task.FromResult(result ?? new PaymentAuthorizationResult(false, null, "Cloud direct should not be called."));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result ?? new PaymentAuthorizationResult(false, null, "Cloud direct should not be called."));
        }
    }

    private sealed class FakeBackendTerminalClient(
        PaymentAuthorizationResult? result = null) : ILinklyBackendTerminalClient
    {
        public int PurchaseCallCount { get; private set; }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(true, "backend ready"));
        }

        public Task<LinklyConnectionTestResult> TestTransactionStatusAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(true, "status ready"));
        }

        public Task<PaymentAuthorizationResult> PurchaseAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default)
        {
            PurchaseCallCount++;
            return Task.FromResult(result ?? new PaymentAuthorizationResult(false, null, "Backend async should not be called."));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result ?? new PaymentAuthorizationResult(false, null, "Backend async should not be called."));
        }

        public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LinklyCloudBackendSessionResponse?>(null);
        }

        public Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudBackendSessionResponse> ResumeSessionUntilFinalAsync(
            CardTerminalSettings settings,
            LinklyCloudBackendSessionResponse activeStatus,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task AcknowledgeSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLinklyEftClientFactory(bool connectResult = true) : ILinklyEftClientFactory
    {
        public FakeLinklyEftClient Client { get; } = new(connectResult);

        public ILinklyEftClient Create()
        {
            return Client;
        }
    }

    private sealed class FakeLinklyEftClient(bool connectResult = true) : ILinklyEftClient
    {
        private bool _responseReturned;

        public int ConnectCallCount { get; private set; }

        public Task<bool> ConnectAsync(
            string hostName,
            int hostPort,
            bool useSsl,
            bool useKeepAlive)
        {
            ConnectCallCount++;
            return Task.FromResult(connectResult);
        }

        public Task<bool> WriteRequestAsync(EFTRequest request)
        {
            return Task.FromResult(true);
        }

        public Task<bool> SendCancelRequestAsync()
        {
            return Task.FromResult(true);
        }

        public Task<EFTResponse?> ReadResponseAsync(CancellationToken cancellationToken)
        {
            if (_responseReturned)
            {
                return Task.FromResult<EFTResponse?>(null);
            }

            _responseReturned = true;
            return Task.FromResult<EFTResponse?>(new EFTTransactionResponse
            {
                Success = true,
                TxnRef = "LOCAL-1",
                ResponseCode = "00",
                ResponseText = "APPROVED",
                AmtPurchase = 10m
            });
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
