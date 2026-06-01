using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
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

    private static CardTerminalSettings CreateSettings(LinklyConnectionMode mode)
    {
        return CardTerminalSettings.FromEnvironment() with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyConnectionMode = mode,
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
    }

    private sealed class FakeLinklyEftClientFactory : ILinklyEftClientFactory
    {
        public FakeLinklyEftClient Client { get; } = new();

        public ILinklyEftClient Create()
        {
            return Client;
        }
    }

    private sealed class FakeLinklyEftClient : ILinklyEftClient
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
            return Task.FromResult(true);
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
