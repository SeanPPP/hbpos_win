using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Square;

namespace Hbpos.Client.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariableTestCollection
{
    public const string Name = "EnvironmentVariableTests";
}

[Collection(EnvironmentVariableTestCollection.Name)]
public sealed class CardTerminalSettingsTests
{
    private const string EnvToken = "opaque-env-square-token";
    private const string SaveToken = "opaque-save-square-token";
    private const string StoredToken = "opaque-stored-square-token";
    private const string LocalToken = "opaque-local-square-token";
    private const string RemoteToken = "opaque-remote-square-token";
    private const string ExistingToken = "opaque-existing-square-token";
    private const string SandboxToken = "opaque-sandbox-square-token";

    [Fact]
    public void FromEnvironment_reads_linkly_configuration()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_CARD_PROCESSOR"] = "linkly",
            ["HBPOS_LINKLY_HOST"] = "192.168.1.50",
            ["HBPOS_LINKLY_PORT"] = "5444",
            ["HBPOS_SQUARE_ACCESS_TOKEN"] = null,
            ["HBPOS_SQUARE_TOKEN"] = null,
            ["SQUARE_TOKEN"] = null,
            ["HBPOS_SQUARE_LOCATION_ID"] = null,
            ["HBPOS_SQUARE_DEVICE_ID"] = null
        });

        var settings = CardTerminalSettings.FromEnvironment();

        Assert.Equal(CardProcessorKind.Linkly, settings.Processor);
        Assert.Equal("192.168.1.50", settings.LinklyHost);
        Assert.Equal(5444, settings.LinklyPort);
    }

    [Theory]
    [InlineData("Local", "LocalIp")]
    [InlineData("LocalIp", "LocalIp")]
    [InlineData("local_ip", "LocalIp")]
    [InlineData("Cloud", "CloudDirectSync")]
    [InlineData("CloudDirectSync", "CloudDirectSync")]
    [InlineData("cloud-direct-sync", "CloudDirectSync")]
    [InlineData("CloudBackendAsync", "CloudBackendAsync")]
    [InlineData("cloud_backend_async", "CloudBackendAsync")]
    public void FromEnvironment_reads_linkly_connection_mode_aliases(string configuredMode, string expectedMode)
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LINKLY_CONNECTION_MODE"] = configuredMode,
            ["HBPOS_LINKLY_MODE"] = null
        });

        var settings = CardTerminalSettings.FromEnvironment();

        Assert.Equal(LinklyMode(expectedMode), settings.LinklyConnectionMode);
    }

    [Fact]
    public void FromEnvironment_reads_linkly_cloud_identity_defaults_and_vendor_id()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_CARD_TERMINAL_ENVIRONMENT"] = null,
            ["HBPOS_LINKLY_POS_NAME"] = null,
            ["HBPOS_LINKLY_POS_VERSION"] = null,
            ["HBPOS_LINKLY_POS_VENDOR_ID"] = "a256b7ec-709d-4c7d-8ffe-57cc7ca1fd22"
        });

        var settings = CardTerminalSettings.FromEnvironment();

        Assert.Equal("HotBargainPOS", settings.LinklyPosName);
        Assert.Equal("2026.5.1", settings.LinklyPosVersion);
        Assert.Equal("a256b7ec-709d-4c7d-8ffe-57cc7ca1fd22", settings.LinklyPosVendorId);
    }

    [Fact]
    public void FromEnvironment_uses_sandbox_placeholder_vendor_id_when_missing()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_CARD_TERMINAL_ENVIRONMENT"] = "Sandbox",
            ["HBPOS_LINKLY_POS_VENDOR_ID"] = null
        });

        var settings = CardTerminalSettings.FromEnvironment();

        Assert.Equal(CardTerminalEnvironment.Sandbox, settings.Environment);
        Assert.Equal(CardTerminalSettings.SandboxPlaceholderLinklyPosVendorId, settings.LinklyPosVendorId);
    }

    [Fact]
    public void FromEnvironment_keeps_production_vendor_id_missing_when_not_configured()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_CARD_TERMINAL_ENVIRONMENT"] = "Production",
            ["HBPOS_LINKLY_POS_VENDOR_ID"] = null
        });

        var settings = CardTerminalSettings.FromEnvironment();

        Assert.Equal(CardTerminalEnvironment.Production, settings.Environment);
        Assert.Null(settings.LinklyPosVendorId);
    }

    [Fact]
    public void FromEnvironment_prefers_environment_specific_linkly_cloud_base_urls_over_legacy_global_variables()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_CARD_TERMINAL_ENVIRONMENT"] = "Sandbox",
            ["HBPOS_LINKLY_CLOUD_AUTH_BASE_URL"] = "https://auth.global.example/v1",
            ["HBPOS_LINKLY_CLOUD_AUTH_BASE_URL_SANDBOX"] = "https://auth.sandbox.example/v1",
            ["HBPOS_LINKLY_CLOUD_REST_BASE_URL"] = "https://rest.global.example/v1",
            ["HBPOS_LINKLY_CLOUD_REST_BASE_URL_SANDBOX"] = "https://rest.sandbox.example/v1"
        });

        var settings = CardTerminalSettings.FromEnvironment();

        Assert.Equal("https://auth.sandbox.example/v1/", settings.LinklyCloudAuthBaseUrl);
        Assert.Equal("https://rest.sandbox.example/v1/", settings.LinklyCloudRestBaseUrl);
    }

    [Fact]
    public void FromEnvironment_does_not_read_square_token_from_environment()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_CARD_PROCESSOR"] = "square",
            ["HBPOS_SQUARE_ACCESS_TOKEN"] = null,
            ["HBPOS_SQUARE_TOKEN"] = EnvToken,
            ["SQUARE_TOKEN"] = null,
            ["HBPOS_SQUARE_LOCATION_ID"] = "LOC-01",
            ["HBPOS_SQUARE_DEVICE_ID"] = "DEV-01"
        });

        var settings = CardTerminalSettings.FromEnvironment();

        Assert.Equal(CardProcessorKind.Square, settings.Processor);
        Assert.Null(settings.SquareAccessToken);
        Assert.Equal("LOC-01", settings.SquareLocationId);
        Assert.Equal("DEV-01", settings.SquareDeviceId);
    }

    [Fact]
    public async Task SaveAsync_protects_square_access_token_before_persisting()
    {
        var repository = new InMemorySettingsRepository();
        var protector = new FakeAuthorizationProtector();
        var store = new CardTerminalSettingsStore(repository, protector);

        await store.SaveAsync(
            CardTerminalConfiguration.Default with
            {
                Processor = CardProcessorKind.Square
            },
            SaveToken);

        AssertSecretEquals(SaveToken, protector.LastProtectedValue, "token should be passed to the protector");
        AssertProtectedTokenEquals(SaveToken, repository.GetStoredValue(TokenKey(CardTerminalEnvironment.Production)));
        Assert.True(
            !string.Equals(SaveToken, repository.GetStoredValue(TokenKey(CardTerminalEnvironment.Production)), StringComparison.Ordinal),
            "stored Square token should not be plaintext");
    }

    [Fact]
    public async Task GetSettingsAsync_unprotects_square_access_token_from_local_store()
    {
        var repository = new InMemorySettingsRepository(new Dictionary<string, string?>
        {
            ["CardTerminal:Processor"] = nameof(CardProcessorKind.Square),
            ["CardTerminal:Environment"] = nameof(CardTerminalEnvironment.Production),
            [TokenKey(CardTerminalEnvironment.Production)] = Protect(StoredToken),
            ["CardTerminal:SquareLocationId"] = "LOC-STORED",
            ["CardTerminal:SquareDeviceId"] = "DEV-STORED"
        });
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        var settings = await store.GetSettingsAsync();

        Assert.Equal(CardProcessorKind.Square, settings.Processor);
        AssertSecretEquals(StoredToken, settings.SquareAccessToken, "local protected token should be unprotected");
        Assert.Equal("LOC-STORED", settings.SquareLocationId);
        Assert.Equal("DEV-STORED", settings.SquareDeviceId);
    }

    [Fact]
    public async Task GetSquareAccessTokenAsync_uses_local_token_without_calling_backend()
    {
        var repository = new InMemorySettingsRepository(new Dictionary<string, string?>
        {
            [TokenKey(CardTerminalEnvironment.Production)] = Protect(LocalToken)
        });
        var apiClient = new FakeSquareTokenApiClient
        {
            Response = new SquareTokenResponse("Production", RemoteToken, DateTimeOffset.UtcNow)
        };
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector(), apiClient);

        var token = await store.GetSquareAccessTokenAsync(
            CardTerminalEnvironment.Production,
            forceRefresh: false);

        AssertSecretEquals(LocalToken, token, "local token should be used without backend call");
        Assert.Equal(0, apiClient.CallCount);
    }

    [Fact]
    public async Task GetSquareAccessTokenAsync_fetches_backend_token_when_local_token_is_missing()
    {
        var repository = new InMemorySettingsRepository();
        var apiClient = new FakeSquareTokenApiClient
        {
            Response = new SquareTokenResponse("Production", RemoteToken, DateTimeOffset.UtcNow)
        };
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector(), apiClient);

        var token = await store.GetSquareAccessTokenAsync(
            CardTerminalEnvironment.Production,
            forceRefresh: false);

        AssertSecretEquals(RemoteToken, token, "backend token should be returned");
        AssertProtectedTokenEquals(RemoteToken, repository.GetStoredValue(TokenKey(CardTerminalEnvironment.Production)));
        Assert.Equal(1, apiClient.CallCount);
    }

    [Fact]
    public async Task GetSquareAccessTokenAsync_keeps_environment_token_caches_separate()
    {
        var repository = new InMemorySettingsRepository(new Dictionary<string, string?>
        {
            [TokenKey(CardTerminalEnvironment.Production)] = Protect(LocalToken),
            [TokenKey(CardTerminalEnvironment.Sandbox)] = Protect(SandboxToken)
        });
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        var productionToken = await store.GetSquareAccessTokenAsync(
            CardTerminalEnvironment.Production,
            forceRefresh: false);
        var sandboxToken = await store.GetSquareAccessTokenAsync(
            CardTerminalEnvironment.Sandbox,
            forceRefresh: false);

        AssertSecretEquals(LocalToken, productionToken, "production token cache should be used");
        AssertSecretEquals(SandboxToken, sandboxToken, "sandbox token cache should be used");
    }

    [Fact]
    public async Task GetSquareAccessTokenAsync_keeps_local_token_when_forced_backend_refresh_fails()
    {
        var repository = new InMemorySettingsRepository(new Dictionary<string, string?>
        {
            [TokenKey(CardTerminalEnvironment.Production)] = Protect(ExistingToken)
        });
        var store = new CardTerminalSettingsStore(
            repository,
            new FakeAuthorizationProtector(),
            new FakeSquareTokenApiClient { Exception = new InvalidOperationException("backend down") });

        var token = await store.GetSquareAccessTokenAsync(
            CardTerminalEnvironment.Production,
            forceRefresh: true);

        Assert.Null(token);
        AssertProtectedTokenEquals(ExistingToken, repository.GetStoredValue(TokenKey(CardTerminalEnvironment.Production)));
    }

    [Fact]
    public async Task SaveAsync_with_empty_square_access_token_keeps_existing_protected_token()
    {
        var repository = new InMemorySettingsRepository(new Dictionary<string, string?>
        {
            [TokenKey(CardTerminalEnvironment.Production)] = Protect(ExistingToken)
        });
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        await store.SaveAsync(
            CardTerminalConfiguration.Default with
            {
                Processor = CardProcessorKind.Square
            },
            "   ");

        AssertProtectedTokenEquals(ExistingToken, repository.GetStoredValue(TokenKey(CardTerminalEnvironment.Production)));
        AssertSecretEquals(ExistingToken, await store.GetSquareAccessTokenAsync(), "existing local token should remain readable");
    }

    [Fact]
    public async Task SaveLinklyCloudSecretAsync_protects_secret_before_persisting()
    {
        const string secret = "opaque-linkly-cloud-secret";
        var repository = new InMemorySettingsRepository();
        var protector = new FakeAuthorizationProtector();
        var store = new CardTerminalSettingsStore(repository, protector);

        await store.SaveLinklyCloudSecretAsync(CardTerminalEnvironment.Production, secret);

        AssertSecretEquals(secret, protector.LastProtectedValue, "Linkly Cloud secret should be passed to the protector");
        Assert.Equal(Protect(secret), repository.GetStoredValue("CardTerminal:LinklyCloudSecretProtected:Production"));
        Assert.True(
            !string.Equals(secret, repository.GetStoredValue("CardTerminal:LinklyCloudSecretProtected:Production"), StringComparison.Ordinal),
            "stored Linkly Cloud secret should not be plaintext");
        AssertSecretEquals(secret, await store.GetLinklyCloudSecretAsync(CardTerminalEnvironment.Production), "protected Linkly Cloud secret should be readable");
    }

    [Fact]
    public async Task SaveLinklyCloudCredentialAsync_protects_password_before_persisting()
    {
        const string username = "cloud-user";
        const string password = "cloud-password";
        var repository = new InMemorySettingsRepository();
        var protector = new FakeAuthorizationProtector();
        var store = new CardTerminalSettingsStore(repository, protector);

        await store.SaveLinklyCloudCredentialAsync(CardTerminalEnvironment.Sandbox, $" {username} ", password);

        Assert.Equal(username, repository.GetStoredValue("CardTerminal:LinklyCloudUsername:Sandbox"));
        AssertSecretEquals(password, protector.LastProtectedValue, "Linkly Cloud API password should be passed to the protector");
        Assert.Equal(Protect(password), repository.GetStoredValue("CardTerminal:LinklyCloudPasswordProtected:Sandbox"));
        Assert.True(
            !string.Equals(password, repository.GetStoredValue("CardTerminal:LinklyCloudPasswordProtected:Sandbox"), StringComparison.Ordinal),
            "stored Linkly Cloud API password should not be plaintext");

        var credential = await store.GetLinklyCloudCredentialAsync(CardTerminalEnvironment.Sandbox);
        Assert.Equal(username, credential.Username);
        AssertSecretEquals(password, credential.Password, "protected Linkly Cloud API password should be readable");
        Assert.True(credential.HasProtectedPassword);
    }

    [Fact]
    public async Task LinklyCloudCredentialAsync_keeps_environment_credentials_separate()
    {
        var repository = new InMemorySettingsRepository();
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        await store.SaveLinklyCloudCredentialAsync(CardTerminalEnvironment.Production, "production-user", "production-password");
        await store.SaveLinklyCloudCredentialAsync(CardTerminalEnvironment.Sandbox, "sandbox-user", "sandbox-password");

        var production = await store.GetLinklyCloudCredentialAsync(CardTerminalEnvironment.Production);
        var sandbox = await store.GetLinklyCloudCredentialAsync(CardTerminalEnvironment.Sandbox);

        Assert.Equal("production-user", production.Username);
        AssertSecretEquals("production-password", production.Password, "production password should be read from production key");
        Assert.Equal("sandbox-user", sandbox.Username);
        AssertSecretEquals("sandbox-password", sandbox.Password, "sandbox password should be read from sandbox key");
    }

    [Fact]
    public async Task GetOrCreateLinklyCloudPosIdAsync_reuses_uuid_v4_for_same_store_and_device()
    {
        var repository = new InMemorySettingsRepository();
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        var first = await store.GetOrCreateLinklyCloudPosIdAsync(CardTerminalEnvironment.Production, "S01", "TERM-1");
        var second = await store.GetOrCreateLinklyCloudPosIdAsync(CardTerminalEnvironment.Production, "S01", "TERM-1");
        var third = await store.GetOrCreateLinklyCloudPosIdAsync(CardTerminalEnvironment.Production, "S01", "TERM-2");

        Assert.Equal(first, second);
        Assert.NotEqual(first, third);
        AssertUuidV4(first);
        AssertUuidV4(third);
    }

    [Fact]
    public async Task GetOrCreateLinklyCloudPosIdAsync_migrates_legacy_key_for_production()
    {
        var legacyPosId = Guid.NewGuid().ToString("D");
        var repository = new InMemorySettingsRepository(new Dictionary<string, string?>
        {
            [LegacyLinklyCloudPosIdKey("S01", "TERM-1")] = legacyPosId
        });
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        var posId = await store.GetOrCreateLinklyCloudPosIdAsync(CardTerminalEnvironment.Production, "S01", "TERM-1");

        Assert.Equal(legacyPosId, posId);
        Assert.Equal(legacyPosId, repository.GetStoredValue(LinklyCloudPosIdKey(CardTerminalEnvironment.Production, "S01", "TERM-1")));
    }

    [Fact]
    public async Task GetOrCreateLinklyCloudPosIdAsync_does_not_reuse_legacy_key_for_sandbox()
    {
        var legacyPosId = Guid.NewGuid().ToString("D");
        var repository = new InMemorySettingsRepository(new Dictionary<string, string?>
        {
            [LegacyLinklyCloudPosIdKey("S01", "TERM-1")] = legacyPosId
        });
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        var posId = await store.GetOrCreateLinklyCloudPosIdAsync(CardTerminalEnvironment.Sandbox, "S01", "TERM-1");

        Assert.NotEqual(legacyPosId, posId);
        AssertUuidV4(posId);
        Assert.Equal(posId, repository.GetStoredValue(LinklyCloudPosIdKey(CardTerminalEnvironment.Sandbox, "S01", "TERM-1")));
        Assert.Null(repository.GetStoredValue(LinklyCloudPosIdKey(CardTerminalEnvironment.Production, "S01", "TERM-1")));
    }

    [Fact]
    public async Task GetOrCreateLinklyCloudPosIdAsync_keeps_environments_separate()
    {
        var repository = new InMemorySettingsRepository();
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        var production = await store.GetOrCreateLinklyCloudPosIdAsync(CardTerminalEnvironment.Production, "S01", "TERM-1");
        var sandbox = await store.GetOrCreateLinklyCloudPosIdAsync(CardTerminalEnvironment.Sandbox, "S01", "TERM-1");
        var productionAgain = await store.GetOrCreateLinklyCloudPosIdAsync(CardTerminalEnvironment.Production, "S01", "TERM-1");
        var sandboxAgain = await store.GetOrCreateLinklyCloudPosIdAsync(CardTerminalEnvironment.Sandbox, "S01", "TERM-1");

        Assert.Equal(production, productionAgain);
        Assert.Equal(sandbox, sandboxAgain);
        Assert.NotEqual(production, sandbox);
        Assert.Equal(production, repository.GetStoredValue(LinklyCloudPosIdKey(CardTerminalEnvironment.Production, "S01", "TERM-1")));
        Assert.Equal(sandbox, repository.GetStoredValue(LinklyCloudPosIdKey(CardTerminalEnvironment.Sandbox, "S01", "TERM-1")));
    }

    [Fact]
    public async Task GetSettingsAsync_prefers_local_configuration_over_environment_variables()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_CARD_PROCESSOR"] = "linkly",
            ["HBPOS_CARD_TERMINAL_ENVIRONMENT"] = "production",
            ["HBPOS_LINKLY_HOST"] = "env-host",
            ["HBPOS_LINKLY_PORT"] = "2333",
            ["HBPOS_SQUARE_ACCESS_TOKEN"] = EnvToken,
            ["HBPOS_SQUARE_LOCATION_ID"] = "ENV-LOC",
            ["HBPOS_SQUARE_DEVICE_ID"] = "ENV-DEV"
        });
        var repository = new InMemorySettingsRepository(new Dictionary<string, string?>
        {
            ["CardTerminal:Processor"] = nameof(CardProcessorKind.Square),
            ["CardTerminal:Environment"] = nameof(CardTerminalEnvironment.Sandbox),
            ["CardTerminal:LinklyHost"] = "local-host",
            ["CardTerminal:LinklyPort"] = "5444",
            [TokenKey(CardTerminalEnvironment.Sandbox)] = Protect(LocalToken),
            ["CardTerminal:SquareLocationId"] = "LOCAL-LOC",
            ["CardTerminal:SquareDeviceId"] = "LOCAL-DEV",
            ["CardTerminal:TimeoutSeconds"] = "120"
        });
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        var settings = await store.GetSettingsAsync();

        Assert.Equal(CardProcessorKind.Square, settings.Processor);
        Assert.Equal(CardTerminalEnvironment.Sandbox, settings.Environment);
        Assert.Equal("local-host", settings.LinklyHost);
        Assert.Equal(5444, settings.LinklyPort);
        AssertSecretEquals(LocalToken, settings.SquareAccessToken, "local token should override environment token");
        Assert.Equal("LOCAL-LOC", settings.SquareLocationId);
        Assert.Equal("LOCAL-DEV", settings.SquareDeviceId);
        Assert.Equal(TimeSpan.FromSeconds(120), settings.TerminalTimeout);
    }

    [Fact]
    public async Task GetSettingsAsync_uses_sandbox_square_base_url_when_environment_is_sandbox()
    {
        var repository = new InMemorySettingsRepository(new Dictionary<string, string?>
        {
            ["CardTerminal:Processor"] = nameof(CardProcessorKind.Square),
            ["CardTerminal:Environment"] = nameof(CardTerminalEnvironment.Sandbox),
            [TokenKey(CardTerminalEnvironment.Sandbox)] = Protect(SandboxToken)
        });
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        var settings = await store.GetSettingsAsync();

        Assert.Equal("https://connect.squareupsandbox.com/v2/", settings.SquareApiBaseUrl);
    }

    [Fact]
    public async Task GetSettingsAsync_uses_linkly_cloud_base_url_environment_overrides()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LINKLY_CLOUD_AUTH_BASE_URL"] = "https://auth.example.test/v1",
            ["HBPOS_LINKLY_CLOUD_REST_BASE_URL"] = "https://rest.example.test/v1/"
        });
        var repository = new InMemorySettingsRepository(new Dictionary<string, string?>
        {
            ["CardTerminal:Processor"] = nameof(CardProcessorKind.Linkly),
            ["CardTerminal:Environment"] = nameof(CardTerminalEnvironment.Sandbox),
            ["CardTerminal:LinklyConnectionMode"] = nameof(LinklyConnectionMode.Cloud)
        });
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        var settings = await store.GetSettingsAsync();

        Assert.Equal("https://auth.example.test/v1/", settings.LinklyCloudAuthBaseUrl);
        Assert.Equal("https://rest.example.test/v1/", settings.LinklyCloudRestBaseUrl);
    }

    [Fact]
    public async Task GetSettingsAsync_prefers_environment_specific_linkly_cloud_base_url_overrides()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LINKLY_CLOUD_AUTH_BASE_URL"] = "https://auth.global.example/v1",
            ["HBPOS_LINKLY_CLOUD_AUTH_BASE_URL_PRODUCTION"] = "https://auth.production.example/v1",
            ["HBPOS_LINKLY_CLOUD_REST_BASE_URL"] = "https://rest.global.example/v1",
            ["HBPOS_LINKLY_CLOUD_REST_BASE_URL_PRODUCTION"] = "https://rest.production.example/v1"
        });
        var repository = new InMemorySettingsRepository(new Dictionary<string, string?>
        {
            ["CardTerminal:Processor"] = nameof(CardProcessorKind.Linkly),
            ["CardTerminal:Environment"] = nameof(CardTerminalEnvironment.Production),
            ["CardTerminal:LinklyConnectionMode"] = nameof(LinklyConnectionMode.Cloud)
        });
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        var settings = await store.GetSettingsAsync();

        Assert.Equal("https://auth.production.example/v1/", settings.LinklyCloudAuthBaseUrl);
        Assert.Equal("https://rest.production.example/v1/", settings.LinklyCloudRestBaseUrl);
    }

    [Fact]
    public async Task GetSettingsAsync_uses_sandbox_placeholder_vendor_id_for_saved_sandbox_environment()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_CARD_TERMINAL_ENVIRONMENT"] = "Production",
            ["HBPOS_LINKLY_POS_VENDOR_ID"] = null
        });
        var repository = new InMemorySettingsRepository(new Dictionary<string, string?>
        {
            ["CardTerminal:Environment"] = nameof(CardTerminalEnvironment.Sandbox)
        });
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        var settings = await store.GetSettingsAsync();

        Assert.Equal(CardTerminalEnvironment.Sandbox, settings.Environment);
        Assert.Equal(CardTerminalSettings.SandboxPlaceholderLinklyPosVendorId, settings.LinklyPosVendorId);
    }

    [Theory]
    [InlineData("Local", "LocalIp")]
    [InlineData("LocalIp", "LocalIp")]
    [InlineData("local_ip", "LocalIp")]
    [InlineData("Cloud", "CloudDirectSync")]
    [InlineData("CloudDirectSync", "CloudDirectSync")]
    [InlineData("cloud-direct-sync", "CloudDirectSync")]
    [InlineData("CloudBackendAsync", "CloudBackendAsync")]
    [InlineData("cloud_backend_async", "CloudBackendAsync")]
    public async Task LoadAsync_reads_linkly_connection_mode_aliases(string storedMode, string expectedMode)
    {
        var repository = new InMemorySettingsRepository(new Dictionary<string, string?>
        {
            ["CardTerminal:LinklyConnectionMode"] = storedMode
        });
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        var configuration = await store.LoadAsync();

        Assert.Equal(LinklyMode(expectedMode), configuration.LinklyConnectionMode);
    }

    [Theory]
    [InlineData("Local", "LocalIp")]
    [InlineData("LocalIp", "LocalIp")]
    [InlineData("Cloud", "CloudDirectSync")]
    [InlineData("CloudDirectSync", "CloudDirectSync")]
    [InlineData("CloudBackendAsync", "CloudBackendAsync")]
    public async Task SaveAsync_persists_canonical_linkly_connection_mode_names(string configuredMode, string expectedStoredMode)
    {
        var repository = new InMemorySettingsRepository();
        var store = new CardTerminalSettingsStore(repository, new FakeAuthorizationProtector());

        await store.SaveAsync(
            CardTerminalConfiguration.Default with
            {
                LinklyConnectionMode = LinklyMode(configuredMode)
            },
            squareAccessToken: null);

        Assert.Equal(expectedStoredMode, repository.GetStoredValue("CardTerminal:LinklyConnectionMode"));
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

    private static string Protect(string token)
    {
        return $"protected:{token}";
    }

    private static string TokenKey(CardTerminalEnvironment environment)
    {
        return $"CardTerminal:SquareAccessTokenProtected:{environment}";
    }

    private static string LinklyCloudPosIdKey(
        CardTerminalEnvironment environment,
        string storeCode,
        string deviceCode)
    {
        return $"CardTerminal:LinklyCloudPosId:{environment}:{NormalizeKeyPart(storeCode)}:{NormalizeKeyPart(deviceCode)}";
    }

    private static string LegacyLinklyCloudPosIdKey(string storeCode, string deviceCode)
    {
        return $"CardTerminal:LinklyCloudPosId:{NormalizeKeyPart(storeCode)}:{NormalizeKeyPart(deviceCode)}";
    }

    private static LinklyConnectionMode LinklyMode(string value)
    {
        return Enum.Parse<LinklyConnectionMode>(value, ignoreCase: true);
    }

    private static string NormalizeKeyPart(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        return string.Concat(trimmed.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
    }

    private static void AssertSecretEquals(string expected, string? actual, string safeMessage)
    {
        Assert.True(
            string.Equals(expected, actual, StringComparison.Ordinal),
            safeMessage);
    }

    private static void AssertProtectedTokenEquals(string expectedPlaintext, string? actualProtected)
    {
        Assert.True(
            string.Equals(Protect(expectedPlaintext), actualProtected, StringComparison.Ordinal),
            "stored Square token should match the protected token value");
    }

    private static void AssertUuidV4(string value)
    {
        Assert.True(Guid.TryParse(value, out var guid), "posId should be a UUID.");
        Assert.Equal(4, (guid.ToByteArray()[7] >> 4) & 0x0F);
    }

    private sealed class InMemorySettingsRepository : ILocalAppSettingsRepository
    {
        private readonly Dictionary<string, string> _values;

        public InMemorySettingsRepository(IReadOnlyDictionary<string, string?>? seedValues = null)
        {
            _values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (seedValues is null)
            {
                return;
            }

            foreach (var entry in seedValues)
            {
                if (entry.Value is not null)
                {
                    _values[entry.Key] = entry.Value;
                }
            }
        }

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public string? GetStoredValue(string key)
        {
            return _values.TryGetValue(key, out var value) ? value : null;
        }
    }

    private sealed class FakeAuthorizationProtector : IDeviceAuthorizationProtector
    {
        public string? LastProtectedValue { get; private set; }

        public string? Protect(string? value)
        {
            LastProtectedValue = value;
            return string.IsNullOrWhiteSpace(value) ? null : CardTerminalSettingsTests.Protect(value);
        }

        public string? Unprotect(string? protectedValue)
        {
            return protectedValue?.StartsWith("protected:", StringComparison.Ordinal) == true
                ? protectedValue["protected:".Length..]
                : null;
        }
    }

    private sealed class FakeSquareTokenApiClient : ISquareTokenApiClient
    {
        public SquareTokenResponse? Response { get; init; }

        public Exception? Exception { get; init; }

        public int CallCount { get; private set; }

        public Task<SquareTokenResponse> GetTokenAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Response!);
        }
    }
}
