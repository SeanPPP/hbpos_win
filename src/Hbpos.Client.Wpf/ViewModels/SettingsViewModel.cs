using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ICardTerminalSetupService _setupService;
    private readonly ILocalizationService? _localization;
    private CardTerminalConfiguration _loadedConfiguration = CardTerminalConfiguration.Default;
    private string? _savedSquareLocationId;
    private string? _savedSquareDeviceId;
    private string? _devicesLoadedForLocationId;

    [ObservableProperty]
    private bool _isSandbox;

    [ObservableProperty]
    private bool _hasSavedSquareToken;

    [ObservableProperty]
    private SquareLocationOption? _selectedSquareLocation;

    [ObservableProperty]
    private SquareDeviceOption? _selectedSquareDevice;

    [ObservableProperty]
    private string _linklyHostText = CardTerminalConfiguration.Default.LinklyHost;

    [ObservableProperty]
    private string _linklyPortText = CardTerminalConfiguration.Default.LinklyPort.ToString();

    [ObservableProperty]
    private string _timeoutSecondsText = CardTerminalConfiguration.Default.TerminalTimeoutSeconds.ToString();

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _linklyConnectionSucceeded;

    [ObservableProperty]
    private string _linklyTestStatusMessage = string.Empty;

    public SettingsViewModel(
        ICardTerminalSetupService setupService,
        ILocalizationService? localization = null)
    {
        _setupService = setupService;
        _localization = localization;
        if (_localization is not null)
        {
            _localization.CultureChanged += (_, _) => RaiseLocalizedProperties();
        }

        LoadCommand = new AsyncRelayCommand(LoadAsync);
        LoadLocationsCommand = new AsyncRelayCommand(LoadLocationsAsync, CanLoadLocations);
        LoadDevicesCommand = new AsyncRelayCommand(LoadDevicesAsync, CanLoadDevices);
        SaveSquareCommand = new AsyncRelayCommand(SaveSquareAsync, CanSaveSquare);
        TestLinklyCommand = new AsyncRelayCommand(TestLinklyAsync, CanTestLinkly);
        SaveLinklyCommand = new AsyncRelayCommand(SaveLinklyAsync, CanSaveLinkly);
        StatusMessage = "Ready.";
    }

    public ObservableCollection<SquareLocationOption> SquareLocations { get; } = [];

    public ObservableCollection<SquareDeviceOption> SquareDevices { get; } = [];

    public IAsyncRelayCommand LoadCommand { get; }

    public IAsyncRelayCommand LoadLocationsCommand { get; }

    public IAsyncRelayCommand LoadDevicesCommand { get; }

    public IAsyncRelayCommand SaveSquareCommand { get; }

    public IAsyncRelayCommand TestLinklyCommand { get; }

    public IAsyncRelayCommand SaveLinklyCommand { get; }

    public string ScreenTitleText => "Settings";

    public string CardTerminalTitleText => "Card Terminal Settings";

    public string SquareTitleText => "Square";

    public string LinklyTitleText => "ANZ Linkly";

    public string SquareTokenStatusText => HasSavedSquareToken
        ? "Local encrypted token cached. HBPOS refreshes it when Square rejects it."
        : "No local token cached. The next Square request will fetch one from HBPOS.";

    public CardTerminalEnvironment SelectedEnvironment => IsSandbox
        ? CardTerminalEnvironment.Sandbox
        : CardTerminalEnvironment.Production;

    public async Task LoadAsync()
    {
        await RunBusyAsync(async () =>
        {
            _loadedConfiguration = await _setupService.LoadConfigurationAsync();
            IsSandbox = _loadedConfiguration.Environment == CardTerminalEnvironment.Sandbox;
            LinklyHostText = _loadedConfiguration.LinklyHost;
            LinklyPortText = _loadedConfiguration.LinklyPort.ToString();
            TimeoutSecondsText = _loadedConfiguration.TerminalTimeoutSeconds.ToString();
            HasSavedSquareToken = _loadedConfiguration.HasProtectedSquareAccessToken;
            _savedSquareLocationId = _loadedConfiguration.SquareLocationId;
            _savedSquareDeviceId = _loadedConfiguration.SquareDeviceId;
            _devicesLoadedForLocationId = null;
            LinklyConnectionSucceeded = false;
            LinklyTestStatusMessage = string.Empty;
            SquareLocations.Clear();
            SquareDevices.Clear();
            SelectedSquareLocation = null;
            SelectedSquareDevice = null;
            StatusMessage = "Settings loaded.";
        });
    }

    private async Task LoadLocationsAsync()
    {
        await RunBusyAsync(async () =>
        {
            SquareLocations.ReplaceWith(await _setupService.ListSquareLocationsAsync(
                accessToken: null,
                SelectedEnvironment));
            SquareDevices.Clear();
            _devicesLoadedForLocationId = null;
            SelectedSquareDevice = null;
            SelectedSquareLocation = SquareLocations.FirstOrDefault(location =>
                string.Equals(location.Id, _savedSquareLocationId, StringComparison.OrdinalIgnoreCase));
            HasSavedSquareToken = true;
            StatusMessage = SquareLocations.Count == 0
                ? "No Square locations returned."
                : $"Loaded {SquareLocations.Count} Square locations.";
        });
    }

    private async Task LoadDevicesAsync()
    {
        if (SelectedSquareLocation is null)
        {
            StatusMessage = "Select a Square location first.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            SquareDevices.ReplaceWith(await _setupService.ListSquareDevicesAsync(
                accessToken: null,
                SelectedEnvironment,
                SelectedSquareLocation.Id));
            _devicesLoadedForLocationId = SelectedSquareLocation.Id;
            SelectedSquareDevice = SquareDevices.FirstOrDefault(device =>
                string.Equals(device.Id, _savedSquareDeviceId, StringComparison.OrdinalIgnoreCase));
            HasSavedSquareToken = true;
            StatusMessage = SquareDevices.Count == 0
                ? "No Square devices returned for this location."
                : $"Loaded {SquareDevices.Count} Square devices.";
        });
    }

    private async Task SaveSquareAsync()
    {
        if (SelectedSquareLocation is null)
        {
            StatusMessage = "Select a Square location first.";
            return;
        }

        if (SelectedSquareDevice is null)
        {
            StatusMessage = "Select a Square device first.";
            return;
        }

        if (!SquareLocations.Any(location => string.Equals(location.Id, SelectedSquareLocation.Id, StringComparison.OrdinalIgnoreCase)) ||
            !SquareDevices.Any(device => string.Equals(device.Id, SelectedSquareDevice.Id, StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(_devicesLoadedForLocationId, SelectedSquareLocation.Id, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Load Square locations and devices before saving.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            var configuration = new CardTerminalConfiguration(
                CardProcessorKind.Square,
                SelectedEnvironment,
                NormalizeHost(LinklyHostText),
                ParsePort(LinklyPortText),
                SelectedSquareLocation.Id,
                SelectedSquareDevice.Id,
                HasSavedSquareToken,
                ParseTimeoutSeconds(TimeoutSecondsText));

            await _setupService.SaveSquareAsync(
                configuration,
                squareAccessToken: null);
            _loadedConfiguration = configuration;
            _savedSquareLocationId = configuration.SquareLocationId;
            _savedSquareDeviceId = configuration.SquareDeviceId;
            HasSavedSquareToken = configuration.HasProtectedSquareAccessToken;
            StatusMessage = "Square terminal settings saved.";
        });
    }

    private async Task TestLinklyAsync()
    {
        await RunBusyAsync(async () =>
        {
            LinklyConnectionSucceeded = false;
            var result = await _setupService.TestLinklyConnectionAsync(
                NormalizeHost(LinklyHostText),
                ParsePort(LinklyPortText),
                TimeSpan.FromSeconds(ParseTimeoutSeconds(TimeoutSecondsText)));
            LinklyConnectionSucceeded = result.Succeeded;
            LinklyTestStatusMessage = result.Message ?? (result.Succeeded
                ? "Linkly EFT-Client connection succeeded."
                : "Linkly EFT-Client connection failed.");
            StatusMessage = LinklyTestStatusMessage;
        });
    }

    private async Task SaveLinklyAsync()
    {
        if (!LinklyConnectionSucceeded)
        {
            StatusMessage = "Test Linkly connection before enabling ANZ Linkly.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            var configuration = _loadedConfiguration with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyHost = NormalizeHost(LinklyHostText),
                LinklyPort = ParsePort(LinklyPortText),
                TerminalTimeoutSeconds = ParseTimeoutSeconds(TimeoutSecondsText)
            };

            await _setupService.SaveLinklyAsync(configuration);
            _loadedConfiguration = configuration;
            StatusMessage = "ANZ Linkly terminal settings saved.";
        });
    }

    private bool CanLoadLocations()
    {
        return !IsBusy;
    }

    private bool CanLoadDevices()
    {
        return !IsBusy && SelectedSquareLocation is not null;
    }

    private bool CanSaveSquare()
    {
        return !IsBusy &&
            SelectedSquareLocation is not null &&
            SelectedSquareDevice is not null &&
            SquareLocations.Any(location => string.Equals(location.Id, SelectedSquareLocation.Id, StringComparison.OrdinalIgnoreCase)) &&
            SquareDevices.Any(device => string.Equals(device.Id, SelectedSquareDevice.Id, StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(_devicesLoadedForLocationId, SelectedSquareLocation.Id, StringComparison.OrdinalIgnoreCase);
    }

    private bool CanTestLinkly()
    {
        return !IsBusy;
    }

    private bool CanSaveLinkly()
    {
        return !IsBusy && LinklyConnectionSucceeded;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Operation canceled.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseLocalizedProperties()
    {
        OnPropertyChanged(nameof(ScreenTitleText));
        OnPropertyChanged(nameof(CardTerminalTitleText));
        OnPropertyChanged(nameof(SquareTitleText));
        OnPropertyChanged(nameof(LinklyTitleText));
        OnPropertyChanged(nameof(SquareTokenStatusText));
    }

    partial void OnIsSandboxChanged(bool value)
    {
        SquareLocations.Clear();
        SquareDevices.Clear();
        _devicesLoadedForLocationId = null;
        SelectedSquareLocation = null;
        SelectedSquareDevice = null;
        RaiseCommandStates();
        OnPropertyChanged(nameof(SelectedEnvironment));
    }

    partial void OnHasSavedSquareTokenChanged(bool value)
    {
        RaiseCommandStates();
        OnPropertyChanged(nameof(SquareTokenStatusText));
    }

    partial void OnSelectedSquareLocationChanged(SquareLocationOption? value)
    {
        if (!string.Equals(_devicesLoadedForLocationId, value?.Id, StringComparison.OrdinalIgnoreCase))
        {
            SquareDevices.Clear();
            SelectedSquareDevice = null;
            _devicesLoadedForLocationId = null;
        }

        RaiseCommandStates();
    }

    partial void OnSelectedSquareDeviceChanged(SquareDeviceOption? value)
    {
        RaiseCommandStates();
    }

    partial void OnLinklyHostTextChanged(string value)
    {
        ResetLinklyConnectionTest();
    }

    partial void OnLinklyPortTextChanged(string value)
    {
        ResetLinklyConnectionTest();
    }

    partial void OnTimeoutSecondsTextChanged(string value)
    {
        ResetLinklyConnectionTest();
    }

    partial void OnLinklyConnectionSucceededChanged(bool value)
    {
        RaiseCommandStates();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        LoadLocationsCommand.NotifyCanExecuteChanged();
        LoadDevicesCommand.NotifyCanExecuteChanged();
        SaveSquareCommand.NotifyCanExecuteChanged();
        TestLinklyCommand.NotifyCanExecuteChanged();
        SaveLinklyCommand.NotifyCanExecuteChanged();
    }

    private void ResetLinklyConnectionTest()
    {
        LinklyConnectionSucceeded = false;
        LinklyTestStatusMessage = string.Empty;
    }

    private static string NormalizeHost(string? host)
    {
        return string.IsNullOrWhiteSpace(host) ? CardTerminalConfiguration.Default.LinklyHost : host.Trim();
    }

    private static int ParsePort(string? text)
    {
        return int.TryParse(text, out var port) && port is > 0 and <= 65535
            ? port
            : CardTerminalConfiguration.Default.LinklyPort;
    }

    private static int ParseTimeoutSeconds(string? text)
    {
        return int.TryParse(text, out var seconds) && seconds > 0
            ? seconds
            : CardTerminalConfiguration.Default.TerminalTimeoutSeconds;
    }
}
