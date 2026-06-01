using System.Net;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Tests;

[Collection(EnvironmentVariableTestCollection.Name)]
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

    [Fact]
    public async Task PairLinklyCloudAsync_uses_local_credentials_and_saves_protected_secret()
    {
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudCredentials[CardTerminalEnvironment.Production] =
            new LinklyCloudCredentialSettings("local-user", "local-password", true);
        var cloudApi = new FakeLinklyCloudApiClient { PairSecret = "cloud-secret" };
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudApiClient: cloudApi);

        var result = await service.PairLinklyCloudAsync(CardTerminalEnvironment.Production, "12345", null, null);

        Assert.True(result.Succeeded);
        Assert.Equal("local-user", cloudApi.LastUsername);
        Assert.Equal("local-password", cloudApi.LastPassword);
        Assert.Equal("12345", cloudApi.LastPairCode);
        Assert.Equal("cloud-secret", store.SavedLinklyCloudSecret);
    }

    [Fact]
    public async Task PairLinklyCloudAsync_uses_current_input_before_saved_credentials()
    {
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox] =
            new LinklyCloudCredentialSettings("saved-user", "saved-password", true);
        var cloudApi = new FakeLinklyCloudApiClient { PairSecret = "cloud-secret" };
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudApiClient: cloudApi);

        var result = await service.PairLinklyCloudAsync(
            CardTerminalEnvironment.Sandbox,
            "12345",
            "current-user",
            "current-password");

        Assert.True(result.Succeeded);
        Assert.Equal("current-user", cloudApi.LastUsername);
        Assert.Equal("current-password", cloudApi.LastPassword);
    }

    [Fact]
    public async Task PairLinklyCloudAsync_blocks_missing_local_credentials()
    {
        var cloudApi = new FakeLinklyCloudApiClient { PairSecret = "cloud-secret" };
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(),
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudApiClient: cloudApi);

        var result = await service.PairLinklyCloudAsync(CardTerminalEnvironment.Production, "12345", null, null);

        Assert.False(result.Succeeded);
        Assert.Equal("Save the Linkly Cloud API username and password first.", result.Message);
        Assert.Null(cloudApi.LastPairCode);
    }

    [Fact]
    public async Task PairLinklyCloudAsync_does_not_require_pos_vendor_id()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LINKLY_POS_VENDOR_ID"] = null
        });
        var store = new FakeCardTerminalSettingsStore();
        var cloudApi = new FakeLinklyCloudApiClient { PairSecret = "cloud-secret" };
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            cloudApi);

        var result = await service.PairLinklyCloudAsync(CardTerminalEnvironment.Production, "12345", "user", "password");

        Assert.True(result.Succeeded);
        Assert.Equal("user", cloudApi.LastUsername);
        Assert.Equal("password", cloudApi.LastPassword);
        Assert.Equal("12345", cloudApi.LastPairCode);
        Assert.Equal("cloud-secret", store.SavedLinklyCloudSecret);
    }

    [Fact]
    public async Task PairLinklyCloudAsync_rejects_sandbox_auth_host_for_production_environment()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LINKLY_CLOUD_AUTH_BASE_URL_PRODUCTION"] = "https://auth.sandbox.cloud.pceftpos.com/v1/"
        });
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudCredentials[CardTerminalEnvironment.Production] =
            new LinklyCloudCredentialSettings("local-user", "local-password", true);
        var cloudApi = new FakeLinklyCloudApiClient { PairSecret = "cloud-secret" };
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudApiClient: cloudApi);

        var result = await service.PairLinklyCloudAsync(CardTerminalEnvironment.Production, "12345", null, null);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "Linkly Cloud Auth endpoint does not match the selected Production environment. Update the configured host and try again.",
            result.Message);
        Assert.Null(cloudApi.LastPairCode);
        Assert.Null(store.SavedLinklyCloudSecret);
    }

    [Fact]
    public async Task SaveLinklyCloudAsync_saves_linkly_processor_with_cloud_mode()
    {
        var store = new FakeCardTerminalSettingsStore();
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient());

        await service.SaveLinklyCloudAsync(CardTerminalConfiguration.Default with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyConnectionMode = LinklyConnectionMode.Local
        });

        Assert.NotNull(store.SavedConfiguration);
        Assert.Equal(CardProcessorKind.Linkly, store.SavedConfiguration!.Processor);
        Assert.Equal(LinklyConnectionMode.CloudDirectSync, store.SavedConfiguration.LinklyConnectionMode);
    }

    [Fact]
    public async Task SaveLinklyCloudAsync_preserves_backend_async_mode()
    {
        var store = new FakeCardTerminalSettingsStore();
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient());

        await service.SaveLinklyCloudAsync(CardTerminalConfiguration.Default with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
        });

        Assert.NotNull(store.SavedConfiguration);
        Assert.Equal(LinklyConnectionMode.CloudBackendAsync, store.SavedConfiguration!.LinklyConnectionMode);
    }

    [Fact]
    public async Task TestLinklyCloudConnectionAsync_uses_secret_and_endpoint_for_requested_environment()
    {
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudSecrets[CardTerminalEnvironment.Production] = "production-secret";
        store.LinklyCloudSecrets[CardTerminalEnvironment.Sandbox] = "sandbox-secret";
        var cloudTerminal = new FakeLinklyCloudTerminalClient
        {
            TestResult = new LinklyConnectionTestResult(true, "sandbox ready")
        };
        var deviceState = new DeviceAuthorizationState();
        deviceState.Set(new DeviceAuthorizationContext("TERM-1", "S01", "HW-1", "AUTH-1"));
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudTerminalClient: cloudTerminal,
            deviceAuthorizationState: deviceState);

        var result = await service.TestLinklyCloudConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.True(result.Succeeded);
        Assert.NotNull(cloudTerminal.LastSettings);
        Assert.Equal(CardTerminalEnvironment.Sandbox, cloudTerminal.LastSettings!.Environment);
        Assert.Equal("sandbox-secret", cloudTerminal.LastSettings.LinklyCloudSecret);
        Assert.Equal("https://auth.sandbox.cloud.pceftpos.com/v1/", cloudTerminal.LastSettings.LinklyCloudAuthBaseUrl);
        Assert.Equal("https://rest.pos.sandbox.cloud.pceftpos.com/v1/", cloudTerminal.LastSettings.LinklyCloudRestBaseUrl);
        Assert.Equal(CardTerminalSettings.SandboxPlaceholderLinklyPosVendorId, cloudTerminal.LastSettings.LinklyPosVendorId);
        Assert.Equal("S01", cloudTerminal.LastStoreCode);
        Assert.Equal("TERM-1", cloudTerminal.LastDeviceCode);
    }

    [Fact]
    public async Task TestLinklyCloudConnectionAsync_rejects_production_rest_host_for_sandbox_environment()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LINKLY_CLOUD_REST_BASE_URL_SANDBOX"] = "https://rest.pos.cloud.pceftpos.com/v1/"
        });
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudSecrets[CardTerminalEnvironment.Sandbox] = "sandbox-secret";
        var cloudTerminal = new FakeLinklyCloudTerminalClient
        {
            TestResult = new LinklyConnectionTestResult(true, "sandbox ready")
        };
        var deviceState = new DeviceAuthorizationState();
        deviceState.Set(new DeviceAuthorizationContext("TERM-1", "S01", "HW-1", "AUTH-1"));
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudTerminalClient: cloudTerminal,
            deviceAuthorizationState: deviceState);

        var result = await service.TestLinklyCloudConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "Linkly Cloud REST endpoint does not match the selected Sandbox environment. Update the configured host and try again.",
            result.Message);
        Assert.Null(cloudTerminal.LastSettings);
    }

    private sealed class FakeCardTerminalSettingsStore : ICardTerminalSettingsStore
    {
        public int ForceRefreshCount { get; private set; }

        public CardTerminalConfiguration? SavedConfiguration { get; private set; }

        public string? SavedLinklyCloudSecret { get; private set; }

        public Dictionary<CardTerminalEnvironment, string?> LinklyCloudSecrets { get; } = [];

        public Dictionary<CardTerminalEnvironment, LinklyCloudCredentialSettings> LinklyCloudCredentials { get; } = [];

        public (CardTerminalEnvironment Environment, string Username, string Password)? SavedLinklyCloudCredential { get; private set; }

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

        public Task<string?> GetLinklyCloudSecretAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LinklyCloudSecrets.TryGetValue(environment, out var secret)
                ? secret
                : "linkly-secret");
        }

        public Task SaveLinklyCloudSecretAsync(
            CardTerminalEnvironment environment,
            string secret,
            CancellationToken cancellationToken = default)
        {
            SavedLinklyCloudSecret = secret;
            return Task.CompletedTask;
        }

        public Task<string> GetOrCreateLinklyCloudPosIdAsync(
            CardTerminalEnvironment environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Guid.NewGuid().ToString("D"));
        }

        public Task<LinklyCloudCredentialSettings> GetLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LinklyCloudCredentials.TryGetValue(environment, out var credential)
                ? credential
                : new LinklyCloudCredentialSettings(null, null, false));
        }

        public Task SaveLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            var credential = new LinklyCloudCredentialSettings(username, password, true);
            LinklyCloudCredentials[environment] = credential;
            SavedLinklyCloudCredential = (environment, username, password);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLinklyCloudTerminalClient : ILinklyCloudTerminalClient
    {
        public LinklyConnectionTestResult TestResult { get; init; } = new(false, "not ready");

        public CardTerminalSettings? LastSettings { get; private set; }

        public string? LastStoreCode { get; private set; }

        public string? LastDeviceCode { get; private set; }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            CardTerminalSettings settings,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken = default)
        {
            LastSettings = settings;
            LastStoreCode = storeCode;
            LastDeviceCode = deviceCode;
            return Task.FromResult(TestResult);
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
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = new(StringComparer.OrdinalIgnoreCase);

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var entry in values)
            {
                _originalValues[entry.Key] = Environment.GetEnvironmentVariable(entry.Key);
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }

        public void Dispose()
        {
            foreach (var entry in _originalValues)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
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

    private sealed class FakeLinklyCloudCredentialApiClient : ILinklyCloudCredentialApiClient
    {
        public Task<LinklyCloudCredentialResponse> GetCredentialAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyCloudCredentialResponse(
                "S01",
                environment.ToString(),
                "store-user",
                "store-password",
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeLinklyCloudApiClient : ILinklyCloudApiClient
    {
        public string PairSecret { get; init; } = "secret";

        public string? LastUsername { get; private set; }

        public string? LastPassword { get; private set; }

        public string? LastPairCode { get; private set; }

        public Task<string> PairAsync(
            string authBaseUrl,
            string username,
            string password,
            string pairCode,
            CancellationToken cancellationToken = default)
        {
            LastUsername = username;
            LastPassword = password;
            LastPairCode = pairCode;
            return Task.FromResult(PairSecret);
        }

        public Task<LinklyCloudToken> GetTokenAsync(
            CardTerminalSettings settings,
            string posId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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
            throw new NotSupportedException();
        }

        public Task<LinklyCloudTransactionResult> SendTransactionAsync(
            CardTerminalSettings settings,
            string token,
            LinklyCloudTransactionRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudTransactionResult> GetTransactionAsync(
            CardTerminalSettings settings,
            string token,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
