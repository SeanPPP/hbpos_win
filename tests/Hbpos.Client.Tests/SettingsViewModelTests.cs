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
    public void ResetTestSalesDataCommand_is_visible_and_enabled_in_debug_when_service_is_configured()
    {
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            resetTestSalesDataAsync: _ => Task.CompletedTask,
            confirmResetTestSalesData: () => true);

        Assert.True(viewModel.IsDebugTestSalesDataResetVisible);
        Assert.True(viewModel.ResetTestSalesDataCommand.CanExecute(null));
    }

    [Fact]
    public async Task ResetTestSalesDataCommand_does_not_call_reset_when_confirmation_is_cancelled()
    {
        var resetCallCount = 0;
        var confirmCallCount = 0;
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            resetTestSalesDataAsync: _ =>
            {
                resetCallCount++;
                return Task.CompletedTask;
            },
            confirmResetTestSalesData: () =>
            {
                confirmCallCount++;
                return false;
            });

        await viewModel.ResetTestSalesDataCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmCallCount);
        Assert.Equal(0, resetCallCount);
        Assert.Equal("Ready.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ResetTestSalesDataCommand_calls_reset_after_confirmation()
    {
        var resetCallCount = 0;
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            resetTestSalesDataAsync: cancellationToken =>
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                resetCallCount++;
                return Task.CompletedTask;
            },
            confirmResetTestSalesData: () => true);

        await viewModel.ResetTestSalesDataCommand.ExecuteAsync(null);

        Assert.Equal(1, resetCallCount);
        Assert.Equal("Local test sales data deleted.", viewModel.StatusMessage);
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
    public async Task LoadAsync_maps_linkly_configuration_to_three_mode_selection()
    {
        var localViewModel = new SettingsViewModel(new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.Local }));
        var cloudViewModel = new SettingsViewModel(new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.Cloud }));
        var backendViewModel = new SettingsViewModel(new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync }));

        await localViewModel.LoadAsync();
        await cloudViewModel.LoadAsync();
        await backendViewModel.LoadAsync();

        Assert.Equal(LinklySettingsMode.LocalIp, localViewModel.SelectedLinklyMode);
        Assert.True(localViewModel.IsLinklyLocalIpMode);
        Assert.False(localViewModel.IsLinklyCloudDirectSyncMode);
        Assert.False(localViewModel.IsLinklyCloudBackendAsyncMode);

        Assert.Equal(LinklySettingsMode.CloudDirectSync, cloudViewModel.SelectedLinklyMode);
        Assert.False(cloudViewModel.IsLinklyLocalIpMode);
        Assert.True(cloudViewModel.IsLinklyCloudDirectSyncMode);
        Assert.False(cloudViewModel.IsLinklyCloudBackendAsyncMode);

        Assert.Equal(LinklySettingsMode.CloudBackendAsync, backendViewModel.SelectedLinklyMode);
        Assert.False(backendViewModel.IsLinklyLocalIpMode);
        Assert.False(backendViewModel.IsLinklyCloudDirectSyncMode);
        Assert.True(backendViewModel.IsLinklyCloudBackendAsyncMode);
    }

    [Fact]
    public async Task LoadAsync_uses_saved_linkly_mode_as_first_priority()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync,
                LinklyConnectionModePriority =
                [
                    LinklyConnectionMode.CloudBackendAsync,
                    LinklyConnectionMode.CloudDirectSync,
                    LinklyConnectionMode.LocalIp
                ]
            }));

        await viewModel.LoadAsync();

        Assert.Equal(LinklySettingsMode.CloudBackendAsync, viewModel.PrimaryLinklyMode);
        Assert.Equal(
            [
                LinklySettingsMode.CloudBackendAsync,
                LinklySettingsMode.CloudDirectSync,
                LinklySettingsMode.LocalIp
            ],
            viewModel.LinklyModePriorityItems.Select(item => item.Mode));
    }

    [Fact]
    public async Task MoveLinklyPriorityUpCommand_promotes_fallback_and_save_persists_priority()
    {
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                LinklyConnectionMode = LinklyConnectionMode.LocalIp,
                LinklyConnectionModePriority =
                [
                    LinklyConnectionMode.LocalIp,
                    LinklyConnectionMode.CloudDirectSync,
                    LinklyConnectionMode.CloudBackendAsync
                ]
            })
        {
            LinklyCloudTestResult = new LinklyConnectionTestResult(true, "cloud ready")
        };
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Production] = true;
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        var cloudDirect = viewModel.LinklyModePriorityItems.Single(item => item.Mode == LinklySettingsMode.CloudDirectSync);
        viewModel.MoveLinklyPriorityUpCommand.Execute(cloudDirect);
        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.Equal(LinklySettingsMode.CloudDirectSync, viewModel.PrimaryLinklyMode);
        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(LinklyConnectionMode.CloudDirectSync, service.SavedConfiguration!.LinklyConnectionMode);
        Assert.Equal(
            [
                LinklyConnectionMode.CloudDirectSync,
                LinklyConnectionMode.LocalIp,
                LinklyConnectionMode.CloudBackendAsync
            ],
            service.SavedConfiguration.LinklyConnectionModePriority);
    }

    [Fact]
    public async Task SelectLinklyPriorityModeCommand_promotes_mode_so_test_and_save_target_match()
    {
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                LinklyConnectionMode = LinklyConnectionMode.LocalIp,
                LinklyConnectionModePriority =
                [
                    LinklyConnectionMode.LocalIp,
                    LinklyConnectionMode.CloudDirectSync,
                    LinklyConnectionMode.CloudBackendAsync
                ]
            })
        {
            LinklyCloudBackendTestResult = new LinklyConnectionTestResult(true, "backend ready")
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        var backend = viewModel.LinklyModePriorityItems.Single(item => item.Mode == LinklySettingsMode.CloudBackendAsync);
        viewModel.SelectLinklyPriorityModeCommand.Execute(backend);
        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.Equal(LinklySettingsMode.CloudBackendAsync, viewModel.PrimaryLinklyMode);
        Assert.Equal(1, service.LinklyCloudBackendTestCallCount);
        Assert.Equal(1, service.SaveLinklyCloudCallCount);
        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(LinklyConnectionMode.CloudBackendAsync, service.SavedConfiguration!.LinklyConnectionMode);
        Assert.Equal(
            [
                LinklyConnectionMode.CloudBackendAsync,
                LinklyConnectionMode.LocalIp,
                LinklyConnectionMode.CloudDirectSync
            ],
            service.SavedConfiguration.LinklyConnectionModePriority);
    }

    [Theory]
    [InlineData("LocalIp", "LocalIp", 1, 0, false)]
    [InlineData("CloudDirectSync", "CloudDirectSync", 0, 1, true)]
    [InlineData("CloudBackendAsync", "CloudBackendAsync", 0, 1, false)]
    public async Task SaveLinklyCommand_persists_and_restores_selected_three_mode(
        string selectedMode,
        string expectedStoredMode,
        int expectedLocalSaveCount,
        int expectedCloudSaveCount,
        bool hasSavedCloudSecret)
    {
        var service = new FakeCardTerminalSetupService();
        var viewModel = new SettingsViewModel(service)
        {
            SelectedLinklyMode = Enum.Parse<LinklySettingsMode>(selectedMode, ignoreCase: true),
            LinklyConnectionSucceeded = true,
            HasSavedLinklyCloudSecret = hasSavedCloudSecret
        };

        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(
            Enum.Parse<LinklyConnectionMode>(expectedStoredMode, ignoreCase: true),
            service.SavedConfiguration!.LinklyConnectionMode);
        Assert.Equal(expectedLocalSaveCount, service.SaveLinklyCallCount);
        Assert.Equal(expectedCloudSaveCount, service.SaveLinklyCloudCallCount);

        var restoredViewModel = new SettingsViewModel(service);
        await restoredViewModel.LoadAsync();

        Assert.Equal(
            Enum.Parse<LinklySettingsMode>(selectedMode, ignoreCase: true),
            restoredViewModel.SelectedLinklyMode);
    }

    [Fact]
    public async Task Changing_linkly_mode_clears_test_status_without_clearing_configuration_fields()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(true, "local connected")
        };
        var viewModel = new SettingsViewModel(service)
        {
            LinklyHostText = "192.168.1.10",
            LinklyPortText = "2011",
            LinklyCloudUsernameText = "cloud-user",
            LinklyCloudPasswordText = "cloud-password",
            LinklyPairCodeText = "123456",
            HasSavedLinklyCloudPassword = true,
            HasSavedLinklyCloudSecret = true
        };

        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        Assert.True(viewModel.LinklyConnectionSucceeded);
        Assert.Equal("local connected", viewModel.LinklyTestStatusMessage);

        viewModel.SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync;

        Assert.False(viewModel.LinklyConnectionSucceeded);
        Assert.Equal(string.Empty, viewModel.LinklyTestStatusMessage);
        Assert.Equal("192.168.1.10", viewModel.LinklyHostText);
        Assert.Equal("2011", viewModel.LinklyPortText);
        Assert.Equal("cloud-user", viewModel.LinklyCloudUsernameText);
        Assert.Equal("cloud-password", viewModel.LinklyCloudPasswordText);
        Assert.Equal("123456", viewModel.LinklyPairCodeText);
        Assert.True(viewModel.HasSavedLinklyCloudPassword);
        Assert.True(viewModel.HasSavedLinklyCloudSecret);
    }

    [Fact]
    public async Task CloudBackendAsync_mode_allows_backend_test_entry_without_local_secret()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudBackendTestResult = new LinklyConnectionTestResult(true, "backend accepted")
        };
        var viewModel = new SettingsViewModel(service)
        {
            SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync,
            IsSandbox = true
        };

        Assert.True(viewModel.IsLinklyCloudMode);
        Assert.True(viewModel.IsLinklyCloudBackendAsyncMode);
        Assert.False(viewModel.HasSavedLinklyCloudSecret);
        Assert.True(viewModel.TestLinklyCommand.CanExecute(null));

        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.Equal("backend accepted", viewModel.LinklyTestStatusMessage);
        Assert.Equal(1, service.LinklyCloudBackendTestCallCount);
        Assert.Equal(0, service.LinklyCloudTestCallCount);
        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(CardTerminalEnvironment.Sandbox, service.SavedConfiguration!.Environment);
        Assert.Equal(LinklyConnectionMode.CloudBackendAsync, service.SavedConfiguration.LinklyConnectionMode);
    }

    [Fact]
    public async Task CloudBackendAsync_status_test_command_runs_backend_status_and_updates_status_bar()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudBackendStatusTestResult = new LinklyConnectionTestResult(false, "DECLINED")
        };
        var viewModel = new SettingsViewModel(service)
        {
            SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync,
            IsSandbox = true
        };
        service.BlockNextLinklyCloudBackendStatusTest();

        Assert.True(viewModel.TestLinklyTransactionStatusCommand.CanExecute(null));

        var execution = viewModel.TestLinklyTransactionStatusCommand.ExecuteAsync(null);

        Assert.False(viewModel.TestLinklyTransactionStatusCommand.CanExecute(null));
        Assert.False(viewModel.TestLinklyCommand.CanExecute(null));

        service.ReleaseLinklyCloudBackendStatusTest();
        await execution;

        Assert.Equal(1, service.LinklyCloudBackendStatusTestCallCount);
        Assert.Equal("DECLINED", viewModel.LinklyTestStatusMessage);
        Assert.Equal("DECLINED", viewModel.StatusMessage);
        Assert.False(viewModel.LinklyConnectionSucceeded);
    }

    [Fact]
    public async Task CloudBackendAsync_status_test_failed_last_transaction_requests_friendly_dialog()
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var dialogService = new RecordingCardRecoveryResultDialogService();
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudBackendStatusTestResult = new LinklyConnectionTestResult(
                false,
                "OPERATOR TIMEOUT",
                new LinklyStatusTestDetails(
                    "session-last",
                    new DateTimeOffset(2026, 6, 10, 9, 30, 0, TimeSpan.Zero),
                    "TM",
                    "OPERATOR TIMEOUT",
                    "txn-last"))
        };
        var viewModel = new SettingsViewModel(
            service,
            localization,
            cardRecoveryResultDialogService: dialogService)
        {
            SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync,
            IsSandbox = true
        };

        await viewModel.TestLinklyTransactionStatusCommand.ExecuteAsync(null);

        var dialog = Assert.Single(dialogService.RequestedDialogs);
        Assert.Equal("上一笔刷卡交易未成功", dialog.Title);
        Assert.Equal("session-last", dialog.SessionId);
        Assert.Equal("txn-last", dialog.TxnRef);
        Assert.Equal("TM", dialog.ResponseCode);
        Assert.Equal("OPERATOR TIMEOUT", dialog.ResponseText);
        Assert.False(dialog.CanPrintReceipt);
    }

    [Fact]
    public async Task CloudBackendAsync_status_test_failed_last_transaction_dialog_uses_english_culture()
    {
        var localization = new LocalizationService();
        localization.SetCulture("en-US");
        var dialogService = new RecordingCardRecoveryResultDialogService();
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudBackendStatusTestResult = new LinklyConnectionTestResult(
                false,
                "OPERATOR TIMEOUT",
                new LinklyStatusTestDetails(
                    "session-last",
                    new DateTimeOffset(2026, 6, 10, 9, 30, 0, TimeSpan.Zero),
                    "TM",
                    "OPERATOR TIMEOUT",
                    "txn-last"))
        };
        var viewModel = new SettingsViewModel(
            service,
            localization,
            cardRecoveryResultDialogService: dialogService)
        {
            SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync,
            IsSandbox = true
        };

        await viewModel.TestLinklyTransactionStatusCommand.ExecuteAsync(null);

        var dialog = Assert.Single(dialogService.RequestedDialogs);
        Assert.Equal("Previous card transaction was not successful", dialog.Title);
        Assert.Contains("Transaction Status", dialog.Message);
    }

    [Fact]
    public async Task CloudBackendAsync_status_test_non_transaction_failure_does_not_request_failed_last_transaction_dialog()
    {
        var dialogService = new RecordingCardRecoveryResultDialogService();
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudBackendStatusTestResult = new LinklyConnectionTestResult(
                false,
                "LOGON REQUIRED",
                new LinklyStatusTestDetails(
                    "status-session",
                    new DateTimeOffset(2026, 6, 10, 9, 35, 0, TimeSpan.Zero),
                    "91",
                    "LOGON REQUIRED",
                    null))
        };
        var viewModel = new SettingsViewModel(
            service,
            cardRecoveryResultDialogService: dialogService)
        {
            SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync,
            IsSandbox = true
        };

        await viewModel.TestLinklyTransactionStatusCommand.ExecuteAsync(null);

        Assert.Empty(dialogService.RequestedDialogs);
    }

    [Fact]
    public async Task CloudBackendAsync_mode_reuses_credential_save_and_pair_commands()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudPairResult = new LinklyConnectionTestResult(true, "paired")
        };
        var viewModel = new SettingsViewModel(service)
        {
            SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync,
            IsSandbox = true,
            LinklyCloudUsernameText = "cloud-user",
            LinklyCloudPasswordText = "cloud-password",
            LinklyPairCodeText = "12345"
        };

        Assert.True(viewModel.SaveLinklyCloudCredentialCommand.CanExecute(null));
        Assert.True(viewModel.PairLinklyCloudCommand.CanExecute(null));

        await viewModel.SaveLinklyCloudCredentialCommand.ExecuteAsync(null);

        Assert.Equal(
            "Linkly Cloud API test account saved securely and synced to HBPOS.",
            viewModel.LinklyTestStatusMessage);
        Assert.Equal(
            "Linkly Cloud API test account saved securely and synced to HBPOS.",
            viewModel.StatusMessage);
        Assert.Equal(CardTerminalEnvironment.Sandbox, service.SavedLinklyCloudCredential?.Environment);
        Assert.True(service.LastSaveLinklyCloudCredentialSyncBackend);

        await viewModel.PairLinklyCloudCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasSavedLinklyCloudSecret);
        Assert.Equal("cloud-user", service.LastPairUsername);
        Assert.Equal(string.Empty, service.LastPairPassword);
        Assert.True(service.LastPairSyncBackendTerminalCredential);
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
            IsSandbox = true,
            LinklyCloudUsernameText = "cloud-user",
            LinklyCloudPasswordText = "cloud-password",
            LinklyPairCodeText = "12345"
        };

        Assert.True(viewModel.PairLinklyCloudCommand.CanExecute(null));

        await viewModel.PairLinklyCloudCommand.ExecuteAsync(null);
        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasSavedLinklyCloudSecret);
        Assert.Equal("cloud-user", service.LastPairUsername);
        Assert.Equal("cloud-password", service.LastPairPassword);
        Assert.False(service.LastPairSyncBackendTerminalCredential);
        Assert.Equal("cloud connected", viewModel.LinklyTestStatusMessage);
        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(CardProcessorKind.Linkly, service.SavedConfiguration!.Processor);
        Assert.Equal(CardTerminalEnvironment.Sandbox, service.SavedConfiguration.Environment);
        Assert.Equal(LinklyConnectionMode.CloudDirectSync, service.SavedConfiguration.LinklyConnectionMode);
    }

    [Fact]
    public async Task PairLinklyCloudCommand_shows_prompt_when_pair_code_is_missing()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudPairResult = new LinklyConnectionTestResult(true, "paired")
        };
        var viewModel = new SettingsViewModel(service)
        {
            IsLinklyCloudMode = true,
            LinklyCloudUsernameText = "cloud-user",
            LinklyCloudPasswordText = "cloud-password"
        };

        Assert.True(viewModel.PairLinklyCloudCommand.CanExecute(null));

        await viewModel.PairLinklyCloudCommand.ExecuteAsync(null);

        Assert.Equal("Enter the Linkly VPP pair code first.", viewModel.LinklyTestStatusMessage);
        Assert.Equal("Enter the Linkly VPP pair code first.", viewModel.StatusMessage);
        Assert.Equal(0, service.PairLinklyCloudCallCount);
    }

    [Fact]
    public async Task SaveLinklyCloudCredentialCommand_saves_test_account_and_clears_password()
    {
        var service = new FakeCardTerminalSetupService();
        var viewModel = new SettingsViewModel(service)
        {
            IsLinklyCloudMode = true,
            IsSandbox = true,
            LinklyCloudUsernameText = "sandbox-user",
            LinklyCloudPasswordText = "sandbox-password"
        };

        Assert.True(viewModel.SaveLinklyCloudCredentialCommand.CanExecute(null));

        await viewModel.SaveLinklyCloudCredentialCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasSavedLinklyCloudPassword);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
        Assert.Equal(CardTerminalEnvironment.Sandbox, service.SavedLinklyCloudCredential?.Environment);
        Assert.Equal("sandbox-user", service.SavedLinklyCloudCredential?.Username);
        Assert.Equal("sandbox-password", service.SavedLinklyCloudCredential?.Password);
        Assert.False(service.LastSaveLinklyCloudCredentialSyncBackend);
        Assert.Equal("Linkly Cloud API test account saved securely.", viewModel.StatusMessage);
        Assert.Equal("Linkly Cloud API test account saved securely.", viewModel.LinklyTestStatusMessage);
    }

    [Fact]
    public void CancelLinklyCloudPairingCommand_clears_pair_code_and_current_password()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService())
        {
            IsLinklyCloudMode = true,
            LinklyCloudUsernameText = "sandbox-user",
            LinklyCloudPasswordText = "sandbox-password",
            LinklyPairCodeText = "123456"
        };

        Assert.True(viewModel.CancelLinklyCloudPairingCommand.CanExecute(null));

        viewModel.CancelLinklyCloudPairingCommand.Execute(null);

        Assert.Equal("sandbox-user", viewModel.LinklyCloudUsernameText);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
        Assert.Equal(string.Empty, viewModel.LinklyPairCodeText);
        Assert.False(viewModel.CancelLinklyCloudPairingCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadAsync_loads_linkly_cloud_username_and_password_status_without_password()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Environment = CardTerminalEnvironment.Sandbox,
            LinklyConnectionMode = LinklyConnectionMode.Cloud
        });
        service.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox] =
            new LinklyCloudCredentialSettings("sandbox-user", "sandbox-password", true);
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();

        Assert.Equal("sandbox-user", viewModel.LinklyCloudUsernameText);
        Assert.True(viewModel.HasSavedLinklyCloudPassword);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
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

    [Fact]
    public async Task LinklyCloud_environment_change_clears_fields_immediately_before_loading_target_state()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Environment = CardTerminalEnvironment.Production,
            LinklyConnectionMode = LinklyConnectionMode.Cloud,
            HasProtectedLinklyCloudSecret = true
        })
        {
            LinklyCloudTestResult = new LinklyConnectionTestResult(true, "cloud connected")
        };
        service.LinklyCloudCredentials[CardTerminalEnvironment.Production] =
            new LinklyCloudCredentialSettings("prod-user", "prod-password", true);
        service.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox] =
            new LinklyCloudCredentialSettings("sandbox-user", "sandbox-password", true);
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Production] = true;
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Sandbox] = true;
        service.BlockNextLinklyCloudCredentialLoad(CardTerminalEnvironment.Sandbox);
        service.BlockNextLinklyCloudSecretStatus(CardTerminalEnvironment.Sandbox);
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        viewModel.LinklyCloudPasswordText = "transient-password";
        viewModel.LinklyPairCodeText = "PAIR123";
        await viewModel.TestLinklyCommand.ExecuteAsync(null);

        Assert.Equal("prod-user", viewModel.LinklyCloudUsernameText);
        Assert.Equal("cloud connected", viewModel.LinklyTestStatusMessage);

        viewModel.IsSandbox = true;

        Assert.Equal(string.Empty, viewModel.LinklyCloudUsernameText);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
        Assert.Equal(string.Empty, viewModel.LinklyPairCodeText);
        Assert.False(viewModel.HasSavedLinklyCloudPassword);
        Assert.False(viewModel.HasSavedLinklyCloudSecret);
        Assert.Equal(string.Empty, viewModel.LinklyTestStatusMessage);

        service.ReleaseLinklyCloudCredentialLoad(CardTerminalEnvironment.Sandbox);
        service.ReleaseLinklyCloudSecretStatus(CardTerminalEnvironment.Sandbox);

        await WaitUntilAsync(() =>
            viewModel.LinklyCloudUsernameText == "sandbox-user" &&
            viewModel.HasSavedLinklyCloudPassword &&
            viewModel.HasSavedLinklyCloudSecret);
    }

    [Fact]
    public async Task LinklyCloud_credential_refresh_ignores_stale_results_after_fast_environment_switches()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Environment = CardTerminalEnvironment.Production,
            LinklyConnectionMode = LinklyConnectionMode.Cloud,
            HasProtectedLinklyCloudSecret = true
        });
        service.LinklyCloudCredentials[CardTerminalEnvironment.Production] =
            new LinklyCloudCredentialSettings("prod-user", "prod-password", true);
        service.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox] =
            new LinklyCloudCredentialSettings("sandbox-user", "sandbox-password", false);
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Production] = true;
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Sandbox] = false;
        service.BlockNextLinklyCloudCredentialLoad(CardTerminalEnvironment.Sandbox);
        service.BlockNextLinklyCloudSecretStatus(CardTerminalEnvironment.Sandbox);
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();

        viewModel.IsSandbox = true;
        viewModel.IsSandbox = false;

        await WaitUntilAsync(() =>
            viewModel.LinklyCloudUsernameText == "prod-user" &&
            viewModel.HasSavedLinklyCloudPassword &&
            viewModel.HasSavedLinklyCloudSecret);

        // 旧环境的异步结果在这里才放行，用来验证不会回写当前环境字段。
        service.ReleaseLinklyCloudCredentialLoad(CardTerminalEnvironment.Sandbox);
        service.ReleaseLinklyCloudSecretStatus(CardTerminalEnvironment.Sandbox);
        await Task.Delay(50);

        Assert.False(viewModel.IsSandbox);
        Assert.Equal("prod-user", viewModel.LinklyCloudUsernameText);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
        Assert.True(viewModel.HasSavedLinklyCloudPassword);
        Assert.True(viewModel.HasSavedLinklyCloudSecret);
    }

    [Fact]
    public async Task LinklyCloud_credential_refresh_does_not_overwrite_user_input_in_same_environment()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Environment = CardTerminalEnvironment.Production,
            LinklyConnectionMode = LinklyConnectionMode.Cloud
        });
        service.LinklyCloudCredentials[CardTerminalEnvironment.Production] =
            new LinklyCloudCredentialSettings("prod-user", "prod-password", true);
        service.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox] =
            new LinklyCloudCredentialSettings("sandbox-loaded", "sandbox-password", true);
        service.BlockNextLinklyCloudCredentialLoad(CardTerminalEnvironment.Sandbox);
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        viewModel.IsSandbox = true;
        viewModel.LinklyCloudUsernameText = "typed-user";
        viewModel.LinklyCloudPasswordText = "typed-password";

        service.ReleaseLinklyCloudCredentialLoad(CardTerminalEnvironment.Sandbox);
        await Task.Delay(50);

        Assert.Equal("typed-user", viewModel.LinklyCloudUsernameText);
        Assert.Equal("typed-password", viewModel.LinklyCloudPasswordText);
    }

    [Fact]
    public async Task SaveLinklyCloudCredentialCommand_does_not_mark_current_environment_after_switch_during_save()
    {
        var service = new FakeCardTerminalSetupService();
        service.BlockNextLinklyCloudCredentialSave();
        var viewModel = new SettingsViewModel(service)
        {
            IsLinklyCloudMode = true,
            IsSandbox = true,
            LinklyCloudUsernameText = "sandbox-user",
            LinklyCloudPasswordText = "sandbox-password"
        };

        var saveTask = viewModel.SaveLinklyCloudCredentialCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsBusy);
        Assert.False(viewModel.CanChangeEnvironment);

        viewModel.IsSandbox = false;
        service.ReleaseLinklyCloudCredentialSave();
        await saveTask;

        Assert.Equal(CardTerminalEnvironment.Sandbox, service.SavedLinklyCloudCredential?.Environment);
        Assert.False(viewModel.HasSavedLinklyCloudPassword);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
    }

    [Fact]
    public async Task PairLinklyCloudCommand_does_not_mark_current_environment_after_switch_during_pair()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudPairResult = new LinklyConnectionTestResult(true, "paired")
        };
        service.BlockNextLinklyCloudPair();
        var viewModel = new SettingsViewModel(service)
        {
            IsLinklyCloudMode = true,
            IsSandbox = true,
            LinklyCloudUsernameText = "sandbox-user",
            LinklyCloudPasswordText = "sandbox-password",
            LinklyPairCodeText = "12345"
        };

        var pairTask = viewModel.PairLinklyCloudCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsBusy);
        Assert.False(viewModel.CanChangeEnvironment);

        viewModel.IsSandbox = false;
        service.ReleaseLinklyCloudPair();
        await pairTask;

        Assert.Equal(1, service.PairLinklyCloudCallCount);
        Assert.True(service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Sandbox]);
        Assert.False(viewModel.HasSavedLinklyCloudSecret);
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

        public LinklyConnectionTestResult LinklyCloudBackendTestResult { get; init; } = new(false, "backend failed");

        public LinklyConnectionTestResult LinklyCloudBackendStatusTestResult { get; init; } = new(false, "status failed");

        public int LinklyCloudTestCallCount { get; private set; }

        public int LinklyCloudBackendTestCallCount { get; private set; }

        public int LinklyCloudBackendStatusTestCallCount { get; private set; }

        public int SaveLinklyCallCount { get; private set; }

        public int SaveLinklyCloudCallCount { get; private set; }

        public Dictionary<CardTerminalEnvironment, bool> LinklyCloudSecretStatuses { get; } = [];

        public Dictionary<CardTerminalEnvironment, LinklyCloudCredentialSettings> LinklyCloudCredentials { get; } = [];

        private readonly Dictionary<CardTerminalEnvironment, TaskCompletionSource<bool>> _pendingLinklyCloudCredentialLoads = [];

        private readonly Dictionary<CardTerminalEnvironment, TaskCompletionSource<bool>> _pendingLinklyCloudSecretStatuses = [];

        private TaskCompletionSource<bool>? _pendingLinklyCloudCredentialSave;

        private TaskCompletionSource<bool>? _pendingLinklyCloudPair;

        private TaskCompletionSource<bool>? _pendingLinklyCloudBackendStatusTest;

        public (CardTerminalEnvironment Environment, string Username, string Password)? SavedLinklyCloudCredential { get; private set; }

        public string? LastPairUsername { get; private set; }

        public string? LastPairPassword { get; private set; }

        public bool LastPairSyncBackendTerminalCredential { get; private set; }

        public bool LastSaveLinklyCloudCredentialSyncBackend { get; private set; }

        public int PairLinklyCloudCallCount { get; private set; }

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
            SaveLinklyCallCount++;
            SavedConfiguration = configuration;
            _configuration = configuration;
            return Task.CompletedTask;
        }

        public async Task<LinklyConnectionTestResult> PairLinklyCloudAsync(
            CardTerminalEnvironment environment,
            string pairCode,
            string? username,
            string? password,
            bool syncBackendTerminalCredential = false,
            CancellationToken cancellationToken = default)
        {
            if (_pendingLinklyCloudPair is not null)
            {
                using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                    _pendingLinklyCloudPair);
                await _pendingLinklyCloudPair.Task;
                _pendingLinklyCloudPair = null;
            }

            PairLinklyCloudCallCount++;
            LastPairUsername = username;
            LastPairPassword = password;
            LastPairSyncBackendTerminalCredential = syncBackendTerminalCredential;
            if (LinklyCloudPairResult.Succeeded)
            {
                LinklyCloudSecretStatuses[environment] = true;
                _configuration = _configuration with { HasProtectedLinklyCloudSecret = true };
            }

            return LinklyCloudPairResult;
        }

        public Task<LinklyCloudCredentialSettings> LoadLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return WaitForLinklyCloudCredentialLoadAsync(environment, cancellationToken);
        }

        public async Task SaveLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            string username,
            string password,
            bool syncBackendCredential = false,
            CancellationToken cancellationToken = default)
        {
            if (_pendingLinklyCloudCredentialSave is not null)
            {
                using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                    _pendingLinklyCloudCredentialSave);
                await _pendingLinklyCloudCredentialSave.Task;
                _pendingLinklyCloudCredentialSave = null;
            }

            SavedLinklyCloudCredential = (environment, username, password);
            LastSaveLinklyCloudCredentialSyncBackend = syncBackendCredential;
            LinklyCloudCredentials[environment] = new LinklyCloudCredentialSettings(username, password, true);
        }

        public Task<LinklyConnectionTestResult> TestLinklyCloudConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            LinklyCloudTestCallCount++;
            return Task.FromResult(LinklyCloudTestResult);
        }

        public Task<LinklyConnectionTestResult> TestLinklyCloudBackendConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            LinklyCloudBackendTestCallCount++;
            return Task.FromResult(LinklyCloudBackendTestResult);
        }

        public async Task<LinklyConnectionTestResult> TestLinklyCloudBackendTransactionStatusAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            LinklyCloudBackendStatusTestCallCount++;
            if (_pendingLinklyCloudBackendStatusTest is not null)
            {
                await _pendingLinklyCloudBackendStatusTest.Task.WaitAsync(cancellationToken);
            }

            return LinklyCloudBackendStatusTestResult;
        }

        public Task<bool> HasLinklyCloudSecretAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return WaitForLinklyCloudSecretStatusAsync(environment, cancellationToken);
        }

        public void BlockNextLinklyCloudCredentialLoad(CardTerminalEnvironment environment)
        {
            _pendingLinklyCloudCredentialLoads[environment] = CreatePendingOperation();
        }

        public void ReleaseLinklyCloudCredentialLoad(CardTerminalEnvironment environment)
        {
            ReleasePendingOperation(_pendingLinklyCloudCredentialLoads, environment);
        }

        public void BlockNextLinklyCloudSecretStatus(CardTerminalEnvironment environment)
        {
            _pendingLinklyCloudSecretStatuses[environment] = CreatePendingOperation();
        }

        public void ReleaseLinklyCloudSecretStatus(CardTerminalEnvironment environment)
        {
            ReleasePendingOperation(_pendingLinklyCloudSecretStatuses, environment);
        }

        public void BlockNextLinklyCloudCredentialSave()
        {
            _pendingLinklyCloudCredentialSave = CreatePendingOperation();
        }

        public void ReleaseLinklyCloudCredentialSave()
        {
            _pendingLinklyCloudCredentialSave?.TrySetResult(true);
        }

        public void BlockNextLinklyCloudPair()
        {
            _pendingLinklyCloudPair = CreatePendingOperation();
        }

        public void ReleaseLinklyCloudPair()
        {
            _pendingLinklyCloudPair?.TrySetResult(true);
        }

        public void BlockNextLinklyCloudBackendStatusTest()
        {
            _pendingLinklyCloudBackendStatusTest = CreatePendingOperation();
        }

        public void ReleaseLinklyCloudBackendStatusTest()
        {
            _pendingLinklyCloudBackendStatusTest?.TrySetResult(true);
        }

        private async Task<LinklyCloudCredentialSettings> WaitForLinklyCloudCredentialLoadAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken)
        {
            if (_pendingLinklyCloudCredentialLoads.TryGetValue(environment, out var pendingOperation))
            {
                using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                    pendingOperation);
                await pendingOperation.Task;
            }

            return LinklyCloudCredentials.TryGetValue(environment, out var credential)
                ? credential
                : new LinklyCloudCredentialSettings(null, null, false);
        }

        private async Task<bool> WaitForLinklyCloudSecretStatusAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken)
        {
            if (_pendingLinklyCloudSecretStatuses.TryGetValue(environment, out var pendingOperation))
            {
                using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                    pendingOperation);
                await pendingOperation.Task;
            }

            if (LinklyCloudSecretStatuses.TryGetValue(environment, out var hasSecret))
            {
                return hasSecret;
            }

            return
                _configuration.Environment == environment &&
                _configuration.HasProtectedLinklyCloudSecret;
        }

        private static TaskCompletionSource<bool> CreatePendingOperation()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static void ReleasePendingOperation(
            IDictionary<CardTerminalEnvironment, TaskCompletionSource<bool>> pendingOperations,
            CardTerminalEnvironment environment)
        {
            if (pendingOperations.Remove(environment, out var pendingOperation))
            {
                pendingOperation.TrySetResult(true);
            }
        }

        public Task SaveLinklyCloudAsync(
            CardTerminalConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            SaveLinklyCloudCallCount++;
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

    private sealed class RecordingCardRecoveryResultDialogService : ICardRecoveryResultDialogService
    {
        public event EventHandler<CardRecoveryResultDialogViewModel>? DialogRequested;

        public List<CardRecoveryResultDialogViewModel> RequestedDialogs { get; } = [];

        public void Show(CardRecoveryResultDialogViewModel dialog)
        {
            RequestedDialogs.Add(dialog);
            DialogRequested?.Invoke(this, dialog);
        }
    }
}
