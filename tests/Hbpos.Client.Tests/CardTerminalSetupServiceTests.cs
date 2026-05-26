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

    private sealed class FakeCardTerminalSettingsStore : ICardTerminalSettingsStore
    {
        public int ForceRefreshCount { get; private set; }

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
