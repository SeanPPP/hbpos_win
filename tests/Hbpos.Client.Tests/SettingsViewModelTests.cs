using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

public sealed class SettingsViewModelTests
{
    private const string CachedToken = "opaque-settings-square-token";

    [Fact]
    public void LoadLocationsCommand_allows_backend_token_fetch()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService());

        Assert.True(viewModel.LoadLocationsCommand.CanExecute(null));
    }

    [Fact]
    public void Settings_defaults_to_data_maintenance_category()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService());

        Assert.Equal(SettingsCategory.DataMaintenance, viewModel.SelectedCategory);
        Assert.True(viewModel.IsDataMaintenanceSelected);
        Assert.False(viewModel.IsPaymentTerminalSelected);
        Assert.False(viewModel.IsDeviceRegistrationSelected);
    }

    [Fact]
    public void Category_commands_switch_selected_category()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService());

        viewModel.SelectPaymentTerminalCommand.Execute(null);

        Assert.Equal(SettingsCategory.PaymentTerminal, viewModel.SelectedCategory);
        Assert.False(viewModel.IsDataMaintenanceSelected);
        Assert.True(viewModel.IsPaymentTerminalSelected);

        viewModel.SelectDeviceRegistrationCommand.Execute(null);

        Assert.Equal(SettingsCategory.DeviceRegistration, viewModel.SelectedCategory);
        Assert.True(viewModel.IsDeviceRegistrationSelected);

        viewModel.SelectDataMaintenanceCommand.Execute(null);

        Assert.Equal(SettingsCategory.DataMaintenance, viewModel.SelectedCategory);
        Assert.True(viewModel.IsDataMaintenanceSelected);
    }

    [Fact]
    public void Maintenance_commands_are_disabled_when_services_are_not_configured()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService());

        Assert.False(viewModel.DownloadCatalogCommand.CanExecute(null));
        Assert.False(viewModel.ResetCatalogCommand.CanExecute(null));
        Assert.False(viewModel.ReregisterDeviceCommand.CanExecute(null));
    }

    [Fact]
    public async Task DownloadCatalogCommand_calls_injected_download_delegate()
    {
        var downloadCallCount = 0;
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            downloadCatalogAsync: cancellationToken =>
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                downloadCallCount++;
                return Task.CompletedTask;
            });

        await viewModel.DownloadCatalogCommand.ExecuteAsync(null);

        Assert.Equal(1, downloadCallCount);
        Assert.Equal("Catalog data download completed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ResetCatalogCommand_calls_injected_reset_delegate()
    {
        var resetCallCount = 0;
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            resetCatalogAsync: cancellationToken =>
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                resetCallCount++;
                return Task.CompletedTask;
            });

        await viewModel.ResetCatalogCommand.ExecuteAsync(null);

        Assert.Equal(1, resetCallCount);
        Assert.Equal("Catalog data reset completed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ReregisterDeviceCommand_calls_injected_reregister_delegate()
    {
        var reregisterCallCount = 0;
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            reregisterDeviceAsync: () =>
            {
                reregisterCallCount++;
                return Task.CompletedTask;
            });

        await viewModel.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.Equal(1, reregisterCallCount);
    }

    [Fact]
    public async Task LoadDevicesCommand_requires_selected_location()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(squareAccessToken: CachedToken));

        await viewModel.LoadAsync();

        Assert.False(viewModel.LoadDevicesCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadAsync_does_not_create_local_square_location_or_device_options()
    {
        var configuration = CardTerminalConfiguration.Default with
        {
            Processor = CardProcessorKind.Square,
            SquareLocationId = "LOCAL-LOC",
            SquareDeviceId = "LOCAL-DEV",
            HasProtectedSquareAccessToken = true
        };
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(configuration, CachedToken));

        await viewModel.LoadAsync();

        Assert.Empty(viewModel.SquareLocations);
        Assert.Empty(viewModel.SquareDevices);
        Assert.Null(viewModel.SelectedSquareLocation);
        Assert.Null(viewModel.SelectedSquareDevice);
        Assert.False(viewModel.SaveSquareCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveSquareCommand_requires_device_loaded_from_square_api()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(squareAccessToken: CachedToken))
        {
            SelectedSquareLocation = new SquareLocationOption("LOC-1", "Main"),
            SelectedSquareDevice = new SquareDeviceOption("DEV-1", "Counter", "AVAILABLE")
        };

        await viewModel.LoadAsync();
        viewModel.SelectedSquareLocation = new SquareLocationOption("LOC-1", "Main");
        viewModel.SelectedSquareDevice = new SquareDeviceOption("DEV-1", "Counter", "AVAILABLE");

        Assert.False(viewModel.SaveSquareCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveSquareCommand_saves_selected_square_terminal_after_remote_lists_are_loaded()
    {
        var service = new FakeCardTerminalSetupService(squareAccessToken: CachedToken);
        var viewModel = new SettingsViewModel(service)
        {
            IsSandbox = true,
            TimeoutSecondsText = "45"
        };

        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);
        viewModel.SelectedSquareDevice = viewModel.SquareDevices.Single();
        await viewModel.SaveSquareCommand.ExecuteAsync(null);

        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(CardProcessorKind.Square, service.SavedConfiguration!.Processor);
        Assert.Equal(CardTerminalEnvironment.Sandbox, service.SavedConfiguration.Environment);
        Assert.Equal("LOC-1", service.SavedConfiguration.SquareLocationId);
        Assert.Equal("DEV-1", service.SavedConfiguration.SquareDeviceId);
        Assert.Equal(45, service.SavedConfiguration.TerminalTimeoutSeconds);
        Assert.Null(service.SavedSquareAccessToken);
        Assert.Equal("Square terminal settings saved.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveLinklyCommand_requires_successful_connection_test()
    {
        var service = new FakeCardTerminalSetupService();
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();

        Assert.False(viewModel.SaveLinklyCommand.CanExecute(null));
        Assert.Null(service.SavedConfiguration);
    }

    [Fact]
    public async Task LoadAsync_requires_fresh_linkly_test_even_when_linkly_was_previously_enabled()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Processor = CardProcessorKind.Linkly
        });
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();

        Assert.False(viewModel.LinklyConnectionSucceeded);
        Assert.False(viewModel.SaveLinklyCommand.CanExecute(null));
    }

    [Fact]
    public async Task TestLinklyCommand_allows_saving_linkly_as_active_processor()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(true, "connected")
        };
        var viewModel = new SettingsViewModel(service)
        {
            LinklyHostText = "192.168.1.10",
            LinklyPortText = "2011",
            TimeoutSecondsText = "180"
        };

        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.Equal("connected", viewModel.LinklyTestStatusMessage);
        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(CardProcessorKind.Linkly, service.SavedConfiguration!.Processor);
        Assert.Equal("192.168.1.10", service.SavedConfiguration.LinklyHost);
        Assert.Equal(2011, service.SavedConfiguration.LinklyPort);
        Assert.Equal("ANZ Linkly terminal settings saved.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task TestLinklyCommand_shows_failed_result_near_linkly_controls()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(false, "connection failed")
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.TestLinklyCommand.ExecuteAsync(null);

        Assert.False(viewModel.LinklyConnectionSucceeded);
        Assert.Equal("connection failed", viewModel.LinklyTestStatusMessage);
        Assert.Equal("connection failed", viewModel.StatusMessage);
    }

    private sealed class FakeCardTerminalSetupService(
        CardTerminalConfiguration? configuration = null,
        string? squareAccessToken = null) : ICardTerminalSetupService
    {
        private CardTerminalConfiguration _configuration = configuration ?? CardTerminalConfiguration.Default;
        private string? _squareAccessToken = squareAccessToken;

        public CardTerminalConfiguration? SavedConfiguration { get; private set; }

        public string? SavedSquareAccessToken { get; private set; }

        public LinklyConnectionTestResult LinklyTestResult { get; init; } = new(false, "failed");

        public Task<CardTerminalConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_configuration);
        }

        public Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_squareAccessToken);
        }

        public Task<IReadOnlyList<SquareLocationOption>> ListSquareLocationsAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(_squareAccessToken))
            {
                throw new InvalidOperationException("Enter a Square Access Token first.");
            }

            IReadOnlyList<SquareLocationOption> locations = [new("LOC-1", "Main")];
            return Task.FromResult(locations);
        }

        public Task<IReadOnlyList<SquareDeviceOption>> ListSquareDevicesAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(_squareAccessToken))
            {
                throw new InvalidOperationException("Enter a Square Access Token first.");
            }

            Assert.Equal("LOC-1", locationId);
            IReadOnlyList<SquareDeviceOption> devices = [new("DEV-1", "Counter", "AVAILABLE")];
            return Task.FromResult(devices);
        }

        public Task SaveSquareAsync(
            CardTerminalConfiguration configuration,
            string? squareAccessToken,
            CancellationToken cancellationToken = default)
        {
            SavedConfiguration = configuration;
            SavedSquareAccessToken = squareAccessToken;
            _configuration = configuration with
            {
                HasProtectedSquareAccessToken = _configuration.HasProtectedSquareAccessToken || !string.IsNullOrWhiteSpace(squareAccessToken)
            };

            if (!string.IsNullOrWhiteSpace(squareAccessToken))
            {
                _squareAccessToken = squareAccessToken;
            }

            return Task.CompletedTask;
        }

        public Task SaveLinklyAsync(
            CardTerminalConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            SavedConfiguration = configuration;
            _configuration = configuration;
            return Task.CompletedTask;
        }

        public Task<LinklyConnectionTestResult> TestLinklyConnectionAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LinklyTestResult);
        }
    }
}
