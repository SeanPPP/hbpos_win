using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Localization;

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

        viewModel.SelectReceiptPrinterCommand.Execute(null);

        Assert.Equal(SettingsCategory.ReceiptPrinter, viewModel.SelectedCategory);
        Assert.True(viewModel.IsReceiptPrinterSelected);
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
                return Task.FromResult(DeviceReregistrationStartResult.StartedWith("Select a new store."));
            });

        await viewModel.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.Equal(1, reregisterCallCount);
    }

    [Fact]
    public async Task ReregisterDeviceCommand_shows_blocked_reason_on_settings_status()
    {
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            reregisterDeviceAsync: () => Task.FromResult(DeviceReregistrationStartResult.Blocked("存在待同步订单。")));

        await viewModel.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.Equal("存在待同步订单。", viewModel.StatusMessage);
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
        Assert.Equal("Square terminal settings saved. The next payment will use Counter.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveSquareCommand_saves_normalized_square_terminal_device_id()
    {
        var service = new FakeCardTerminalSetupService(squareAccessToken: CachedToken)
        {
            SquareDevicesResult = [new("device:533CS145C3000413", "Square Terminal 0413", "AVAILABLE")]
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);
        viewModel.SelectedSquareDevice = viewModel.SquareDevices.Single();
        await viewModel.SaveSquareCommand.ExecuteAsync(null);

        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal("533CS145C3000413", service.SavedConfiguration!.SquareDeviceId);
    }

    [Fact]
    public async Task SaveSquareCommand_switches_to_another_device_in_same_location()
    {
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                Processor = CardProcessorKind.Square,
                SquareLocationId = "LOC-1",
                SquareDeviceId = "DEV-1",
                HasProtectedSquareAccessToken = true
            },
            CachedToken)
        {
            SquareDevicesResult =
            [
                new("DEV-1", "Counter 1", "AVAILABLE"),
                new("DEV-2", "Counter 2", "AVAILABLE")
            ]
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);
        viewModel.SelectedSquareDevice = viewModel.SquareDevices.Last();

        Assert.Equal("Selected Counter 2. Save Square to switch the next payment to this terminal.", viewModel.StatusMessage);

        await viewModel.SaveSquareCommand.ExecuteAsync(null);

        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal("DEV-2", service.SavedConfiguration!.SquareDeviceId);
        Assert.Equal("Square terminal settings saved. The next payment will use Counter 2.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Device_code_commands_are_disabled_in_sandbox_mode()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                Environment = CardTerminalEnvironment.Sandbox
            },
            CachedToken));

        await viewModel.LoadAsync();

        Assert.False(viewModel.IsSquareDeviceCodesSupported);
        Assert.False(viewModel.LoadDeviceCodesCommand.CanExecute(null));
        Assert.False(viewModel.CreateDeviceCodeCommand.CanExecute(null));
        Assert.False(viewModel.RefreshDeviceCodeStatusCommand.CanExecute(null));
    }

    [Fact]
    public async Task CreateDeviceCodeCommand_creates_and_selects_new_code()
    {
        var service = new FakeCardTerminalSetupService(squareAccessToken: CachedToken)
        {
            CreateDeviceCodeResult = new("DC-1", "Counter 3", "PAIR123", "UNPAIRED", "LOC-1", null, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow)
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        viewModel.SquareDeviceCodeNameText = "Counter 3";
        await viewModel.CreateDeviceCodeCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastCreatedDeviceCodeRequest);
        Assert.Equal("LOC-1", service.LastCreatedDeviceCodeRequest!.Value.LocationId);
        Assert.Equal("Counter 3", service.LastCreatedDeviceCodeRequest.Value.Name);
        Assert.Single(viewModel.SquareDeviceCodes);
        Assert.Equal("PAIR123", viewModel.SelectedSquareDeviceCode!.Code);
        Assert.Equal("Created device code PAIR123 for Counter 3. Enter it on the Square Terminal, then refresh status.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task RefreshDeviceCodeStatusCommand_pairs_and_selects_matching_device_without_saving()
    {
        var service = new FakeCardTerminalSetupService(squareAccessToken: CachedToken)
        {
            SquareDevicesResult =
            [
                new("DEV-1", "Counter 1", "AVAILABLE"),
                new("DEV-2", "Counter 2", "AVAILABLE")
            ],
            GetDeviceCodeResult = new("DC-1", "Counter 2", "PAIR123", "PAIRED", "LOC-1", "DEV-2", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow)
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);
        await viewModel.CreateDeviceCodeCommand.ExecuteAsync(null);
        await viewModel.RefreshDeviceCodeStatusCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.SelectedSquareDevice);
        Assert.Equal("DEV-2", viewModel.SelectedSquareDevice!.Id);
        Assert.Null(service.SavedConfiguration);
        Assert.Equal("Device code paired successfully. Counter 2 is selected and ready to save for the next payment.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task RefreshDeviceCodeStatusCommand_matches_devices_api_id_to_device_code_id()
    {
        var service = new FakeCardTerminalSetupService(squareAccessToken: CachedToken)
        {
            SquareDevicesResult =
            [
                new("device:533CS145C3000413", "Square Terminal 0413", "AVAILABLE")
            ],
            GetDeviceCodeResult = new("DC-1", "Square Terminal 0413", "PAIR123", "PAIRED", "LOC-1", "533CS145C3000413", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow)
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);
        await viewModel.CreateDeviceCodeCommand.ExecuteAsync(null);
        await viewModel.RefreshDeviceCodeStatusCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.SelectedSquareDevice);
        Assert.Equal("device:533CS145C3000413", viewModel.SelectedSquareDevice!.Id);
        Assert.Null(service.SavedConfiguration);
    }

    [Fact]
    public async Task LoadDevicesCommand_selects_saved_device_when_saved_id_has_devices_api_prefix()
    {
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                Processor = CardProcessorKind.Square,
                SquareLocationId = "LOC-1",
                SquareDeviceId = "device:533CS145C3000413",
                HasProtectedSquareAccessToken = true
            },
            CachedToken)
        {
            SquareDevicesResult =
            [
                new("device:533CS145C3000413", "Square Terminal 0413", "AVAILABLE")
            ]
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.SelectedSquareDevice);
        Assert.Equal("device:533CS145C3000413", viewModel.SelectedSquareDevice!.Id);
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

    [Fact]
    public async Task LinklyCloud_commands_pair_test_and_save_cloud_mode()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudPairResult = new LinklyConnectionTestResult(true, "paired"),
            LinklyCloudTestResult = new LinklyConnectionTestResult(true, "cloud connected")
        };
        var viewModel = new SettingsViewModel(service)
        {
            IsLinklyCloudMode = true,
            LinklyPairCodeText = "12345"
        };

        Assert.True(viewModel.PairLinklyCloudCommand.CanExecute(null));

        await viewModel.PairLinklyCloudCommand.ExecuteAsync(null);
        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasSavedLinklyCloudSecret);
        Assert.Equal("cloud connected", viewModel.LinklyTestStatusMessage);
        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(CardProcessorKind.Linkly, service.SavedConfiguration!.Processor);
        Assert.Equal(LinklyConnectionMode.Cloud, service.SavedConfiguration.LinklyConnectionMode);
    }

    [Fact]
    public async Task LinklyCloud_secret_status_refreshes_when_environment_changes()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Environment = CardTerminalEnvironment.Production,
            LinklyConnectionMode = LinklyConnectionMode.Cloud,
            HasProtectedLinklyCloudSecret = true
        });
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Production] = true;
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Sandbox] = true;
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        viewModel.IsSandbox = true;

        await WaitUntilAsync(() => viewModel.HasSavedLinklyCloudSecret);
        Assert.True(viewModel.TestLinklyCommand.CanExecute(null));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task LoadAsync_loads_receipt_printer_settings_and_save_persists_changes()
    {
        var store = new FakeReceiptPrinterSettingsStore
        {
            Settings = ReceiptPrinterSettings.Default with
            {
                PrinterPort = "COM3",
                BrandName = "HB",
                StoreName = "Sunnybank",
                StoreAddress = "Shop 1",
                StorePhone = "07",
                Abn = "ABN",
                ReturnPolicy = "Returns within 7 days"
            }
        };
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: store,
            receiptPrintService: new FakeReceiptPrintService());

        await viewModel.LoadAsync();

        Assert.Equal("COM3", viewModel.ReceiptPrinterPortText);
        Assert.Equal("HB", viewModel.ReceiptBrandNameText);
        Assert.Equal("Sunnybank", viewModel.ReceiptStoreNameText);

        viewModel.ReceiptPrinterPortText = "USB,";
        viewModel.ReceiptStorePhoneText = "0730000000";
        await viewModel.SaveReceiptPrinterCommand.ExecuteAsync(null);

        Assert.NotNull(store.SavedSettings);
        Assert.Equal("USB,", store.SavedSettings!.PrinterPort);
        Assert.Equal("0730000000", store.SavedSettings.StorePhone);
        Assert.Equal("Receipt printer settings saved.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task TestReceiptPrinterCommand_calls_print_service()
    {
        var printService = new FakeReceiptPrintService
        {
            TestResult = new ReceiptPrintResult(true, "Printer test completed.")
        };
        var store = new FakeReceiptPrinterSettingsStore();
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: store,
            receiptPrintService: printService);
        viewModel.ReceiptPrinterPortText = "COM7";

        await viewModel.TestReceiptPrinterCommand.ExecuteAsync(null);

        Assert.Equal(1, printService.TestCallCount);
        Assert.Equal("COM7", store.SavedSettings?.PrinterPort);
        Assert.Equal("Printer test completed.", viewModel.ReceiptPrinterTestStatusMessage);
        Assert.Equal("Printer test completed.", viewModel.StatusMessage);
    }

    [Fact]
    public void Localized_properties_and_status_refresh_when_culture_changes()
    {
        var localization = new LocalizationService();
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(), localization);

        Assert.Equal("Settings", viewModel.ScreenTitleText);
        Assert.Equal("Ready.", viewModel.StatusMessage);

        localization.SetCulture("zh-CN");

        Assert.Equal("\u8BBE\u7F6E", viewModel.ScreenTitleText);
        Assert.Equal("\u5C31\u7EEA\u3002", viewModel.StatusMessage);
        Assert.Equal("\u6570\u636E\u7EF4\u62A4", viewModel.DataMaintenanceTitleText);
        Assert.Equal("\u66F4\u6362\u5206\u5E97\u6CE8\u518C", viewModel.DeviceRegistrationTitleText);
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

        public LinklyConnectionTestResult LinklyCloudPairResult { get; init; } = new(false, "pair failed");

        public LinklyConnectionTestResult LinklyCloudTestResult { get; init; } = new(false, "cloud failed");

        public Dictionary<CardTerminalEnvironment, bool> LinklyCloudSecretStatuses { get; } = [];

        public IReadOnlyList<SquareLocationOption> SquareLocationsResult { get; init; } = [new("LOC-1", "Main")];

        public IReadOnlyList<SquareDeviceOption> SquareDevicesResult { get; set; } = [new("DEV-1", "Counter", "AVAILABLE")];

        public IReadOnlyList<SquareDeviceCodeOption> SquareDeviceCodesResult { get; set; } = [];

        public SquareDeviceCodeOption CreateDeviceCodeResult { get; set; } =
            new("DC-1", "Counter", "PAIR123", "UNPAIRED", "LOC-1", null, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);

        public SquareDeviceCodeOption GetDeviceCodeResult { get; set; } =
            new("DC-1", "Counter", "PAIR123", "UNPAIRED", "LOC-1", null, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);

        public (string LocationId, string Name)? LastCreatedDeviceCodeRequest { get; private set; }

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

            return Task.FromResult(SquareLocationsResult);
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
            return Task.FromResult(SquareDevicesResult);
        }

        public Task<IReadOnlyList<SquareDeviceCodeOption>> ListSquareDeviceCodesAsync(
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
            return Task.FromResult(SquareDeviceCodesResult);
        }

        public Task<SquareDeviceCodeOption> CreateSquareDeviceCodeAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            string name,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(_squareAccessToken))
            {
                throw new InvalidOperationException("Enter a Square Access Token first.");
            }

            LastCreatedDeviceCodeRequest = (locationId, name);
            SquareDeviceCodesResult = [CreateDeviceCodeResult, .. SquareDeviceCodesResult];
            return Task.FromResult(CreateDeviceCodeResult);
        }

        public Task<SquareDeviceCodeOption> GetSquareDeviceCodeAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string deviceCodeId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(_squareAccessToken))
            {
                throw new InvalidOperationException("Enter a Square Access Token first.");
            }

            Assert.Equal("DC-1", deviceCodeId);
            return Task.FromResult(GetDeviceCodeResult);
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

        public Task<LinklyConnectionTestResult> PairLinklyCloudAsync(
            CardTerminalEnvironment environment,
            string pairCode,
            CancellationToken cancellationToken = default)
        {
            if (LinklyCloudPairResult.Succeeded)
            {
                LinklyCloudSecretStatuses[environment] = true;
                _configuration = _configuration with { HasProtectedLinklyCloudSecret = true };
            }

            return Task.FromResult(LinklyCloudPairResult);
        }

        public Task<LinklyConnectionTestResult> TestLinklyCloudConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LinklyCloudTestResult);
        }

        public Task<bool> HasLinklyCloudSecretAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            if (LinklyCloudSecretStatuses.TryGetValue(environment, out var hasSecret))
            {
                return Task.FromResult(hasSecret);
            }

            return Task.FromResult(
                _configuration.Environment == environment &&
                _configuration.HasProtectedLinklyCloudSecret);
        }

        public Task SaveLinklyCloudAsync(
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

    private sealed class FakeReceiptPrinterSettingsStore : IReceiptPrinterSettingsStore
    {
        public ReceiptPrinterSettings Settings { get; set; } = ReceiptPrinterSettings.Default;

        public ReceiptPrinterSettings? SavedSettings { get; private set; }

        public Task<ReceiptPrinterSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Settings);
        }

        public Task SaveAsync(ReceiptPrinterSettings settings, CancellationToken cancellationToken = default)
        {
            SavedSettings = settings;
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReceiptPrintService : IReceiptPrintService
    {
        public ReceiptPrintResult TestResult { get; init; } = new(true, "Printer test completed.");

        public int TestCallCount { get; private set; }

        public Task<ReceiptPrintResult> PrintLatestReceiptAsync(
            ReceiptPrintReason reason = ReceiptPrintReason.LastReceipt,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ReceiptPrintResult> PrintReceiptAsync(
            Guid orderGuid,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ReceiptPrintResult> PrintReceiptAsync(
            ReceiptDetails receipt,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ReceiptPrintResult> TestPrinterAsync(CancellationToken cancellationToken = default)
        {
            TestCallCount++;
            return Task.FromResult(TestResult);
        }
    }
}
