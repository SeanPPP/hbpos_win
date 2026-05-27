using System.Net;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class CardTerminalSetupServiceTests
{
    private const string LocalToken = "opaque-local-setup-token";
    private const string RemoteToken = "opaque-remote-setup-token";

    [Fact]
    public async Task ListSquareLocationsAsync_refreshes_token_once_after_auth_failure()
    {
        var store = new FakeCardTerminalSettingsStore();
        var squareClient = new FakeSquareTerminalSetupClient
        {
            FailFirstRequestWithAuthError = true
        };
        var service = new CardTerminalSetupService(store, squareClient, new FakeLinklyTerminalClient());

        var locations = await service.ListSquareLocationsAsync(null, CardTerminalEnvironment.Production);

        Assert.Single(locations);
        Assert.True(
            squareClient.UsedTokenSequence(LocalToken, RemoteToken),
            "setup service should retry with refreshed token after Square auth failure");
        Assert.Equal(1, store.ForceRefreshCount);
    }

    [Fact]
    public async Task CreateSquareDeviceCodeAsync_refreshes_token_once_after_auth_failure()
    {
        var store = new FakeCardTerminalSettingsStore();
        var squareClient = new FakeSquareTerminalSetupClient
        {
            FailFirstRequestWithAuthError = true
        };
        var service = new CardTerminalSetupService(store, squareClient, new FakeLinklyTerminalClient());

        var result = await service.CreateSquareDeviceCodeAsync(null, CardTerminalEnvironment.Production, "LOC-1", "Counter 2");

        Assert.Equal("PAIR123", result.Code);
        Assert.True(
            squareClient.UsedTokenSequence(LocalToken, RemoteToken),
            "setup service should retry device code creation with refreshed token after Square auth failure");
        Assert.Equal(1, store.ForceRefreshCount);
    }

    [Fact]
    public async Task Device_code_operations_are_blocked_in_sandbox()
    {
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(),
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ListSquareDeviceCodesAsync(null, CardTerminalEnvironment.Sandbox, "LOC-1"));

        Assert.Equal("Square Device Codes are only supported in Production.", exception.Message);
    }

    [Fact]
    public async Task SaveSquareAsync_normalizes_devices_api_device_id_before_saving()
    {
        var store = new FakeCardTerminalSettingsStore();
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient());
        var configuration = CardTerminalConfiguration.Default with
        {
            Processor = CardProcessorKind.Square,
            SquareLocationId = "LOC-1",
            SquareDeviceId = "device:533CS145C3000413"
        };

        await service.SaveSquareAsync(configuration, squareAccessToken: null);

        Assert.NotNull(store.SavedConfiguration);
        Assert.Equal("533CS145C3000413", store.SavedConfiguration!.SquareDeviceId);
    }

    private sealed class FakeCardTerminalSettingsStore : ICardTerminalSettingsStore
    {
        public int ForceRefreshCount { get; private set; }

        public CardTerminalConfiguration? SavedConfiguration { get; private set; }

        public Task<CardTerminalConfiguration> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CardTerminalConfiguration.Default with
            {
                Processor = CardProcessorKind.Square,
                HasProtectedSquareAccessToken = true
            });
        }

        public Task SaveAsync(
            CardTerminalConfiguration configuration,
            string? squareAccessToken,
            CancellationToken cancellationToken = default)
        {
            SavedConfiguration = configuration;
            return Task.CompletedTask;
        }

        public Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(LocalToken);
        }

        public Task<string?> GetSquareAccessTokenAsync(
            CardTerminalEnvironment environment,
            bool forceRefresh,
            CancellationToken cancellationToken = default)
        {
            if (forceRefresh)
            {
                ForceRefreshCount++;
                return Task.FromResult<string?>(RemoteToken);
            }

            return Task.FromResult<string?>(LocalToken);
        }

        public Task<string?> GetTokenAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return GetSquareAccessTokenAsync(environment, forceRefresh: false, cancellationToken);
        }

        public Task<string?> RefreshTokenAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return GetSquareAccessTokenAsync(environment, forceRefresh: true, cancellationToken);
        }

        public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Square,
                SquareAccessToken = LocalToken
            });
        }
    }

    private sealed class FakeSquareTerminalSetupClient : ISquareTerminalSetupClient
    {
        public bool FailFirstRequestWithAuthError { get; init; }

        public List<string> Tokens { get; } = [];

        public Task<IReadOnlyList<SquareLocationOption>> ListLocationsAsync(
            string accessToken,
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            Tokens.Add(accessToken);
            if (FailFirstRequestWithAuthError && Tokens.Count == 1)
            {
                throw new SquareApiException("Square locations request failed with status 401 (Unauthorized).", HttpStatusCode.Unauthorized);
            }

            IReadOnlyList<SquareLocationOption> locations = [new("LOC-1", "Main")];
            return Task.FromResult(locations);
        }

        public bool UsedTokenSequence(string firstToken, string secondToken)
        {
            return Tokens.Count == 2 &&
                string.Equals(Tokens[0], firstToken, StringComparison.Ordinal) &&
                string.Equals(Tokens[1], secondToken, StringComparison.Ordinal);
        }

        public Task<IReadOnlyList<SquareDeviceOption>> ListDevicesAsync(
            string accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<SquareDeviceCodeOption>> ListDeviceCodesAsync(
            string accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SquareDeviceCodeOption> CreateDeviceCodeAsync(
            string accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            string name,
            CancellationToken cancellationToken = default)
        {
            Tokens.Add(accessToken);
            if (FailFirstRequestWithAuthError && Tokens.Count == 1)
            {
                throw new SquareApiException("Square create device code request failed with status 401 (Unauthorized).", HttpStatusCode.Unauthorized);
            }

            return Task.FromResult(new SquareDeviceCodeOption(
                "DC-1",
                name,
                "PAIR123",
                "UNPAIRED",
                locationId,
                null,
                DateTimeOffset.UtcNow.AddMinutes(5),
                DateTimeOffset.UtcNow));
        }

        public Task<SquareDeviceCodeOption> GetDeviceCodeAsync(
            string accessToken,
            CardTerminalEnvironment environment,
            string deviceCodeId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeLinklyTerminalClient : ILinklyTerminalClient
    {
        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(false));
        }

        public Task<PaymentAuthorizationResult> PurchaseAsync(
            decimal amount,
            Hbpos.Client.Wpf.Models.PosSessionState session,
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            Hbpos.Client.Wpf.Models.PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentAuthorizationResult> VoidAsync(
            decimal amount,
            Hbpos.Client.Wpf.Models.PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
