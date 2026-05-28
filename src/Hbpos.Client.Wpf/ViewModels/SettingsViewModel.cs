using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

public enum SettingsCategory
{
    DataMaintenance,
    PaymentTerminal,
    ReceiptPrinter,
    DeviceRegistration
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private const string DefaultSquareDeviceCodeName = "HBPOS Terminal";

    private readonly ICardTerminalSetupService _setupService;
    private readonly ILocalizationService? _localization;
    private readonly Func<CancellationToken, Task>? _downloadCatalogAsync;
    private readonly Func<CancellationToken, Task>? _resetCatalogAsync;
    private readonly Func<Task>? _reregisterDeviceAsync;
    private readonly Action? _returnToPos;
    private readonly IReceiptPrinterSettingsStore? _receiptPrinterSettingsStore;
    private readonly IReceiptPrintService? _receiptPrintService;
    private CardTerminalConfiguration _loadedConfiguration = CardTerminalConfiguration.Default;
    private string? _savedSquareLocationId;
    private string? _savedSquareDeviceId;
    private string? _devicesLoadedForLocationId;
    private string _statusKey = "settings.status.ready";
    private object[] _statusArgs = [];
    private string? _statusOverride;
    private string? _linklyTestStatusKey;
    private object[] _linklyTestStatusArgs = [];
    private string? _linklyTestStatusOverride;
    private string? _receiptPrinterTestStatusOverride;
    private string _lastSquareDeviceCodeNameSuggestion = DefaultSquareDeviceCodeName;
    private int _linklySecretStatusVersion;

    [ObservableProperty]
    private SettingsCategory _selectedCategory = SettingsCategory.DataMaintenance;

    [ObservableProperty]
    private bool _isSandbox;

    [ObservableProperty]
    private bool _hasSavedSquareToken;

    [ObservableProperty]
    private SquareLocationOption? _selectedSquareLocation;

    [ObservableProperty]
    private SquareDeviceOption? _selectedSquareDevice;

    [ObservableProperty]
    private SquareDeviceCodeOption? _selectedSquareDeviceCode;

    [ObservableProperty]
    private string _squareDeviceCodeNameText = DefaultSquareDeviceCodeName;

    [ObservableProperty]
    private string _linklyHostText = CardTerminalConfiguration.Default.LinklyHost;

    [ObservableProperty]
    private string _linklyPortText = CardTerminalConfiguration.Default.LinklyPort.ToString();

    [ObservableProperty]
    private bool _isLinklyCloudMode;

    [ObservableProperty]
    private string _linklyPairCodeText = string.Empty;

    [ObservableProperty]
    private bool _hasSavedLinklyCloudSecret;

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

    [ObservableProperty]
    private string _receiptPrinterPortText = ReceiptPrinterSettings.Default.PrinterPort;

    [ObservableProperty]
    private string _receiptBrandNameText = ReceiptPrinterSettings.Default.BrandName;

    [ObservableProperty]
    private string _receiptStoreNameText = string.Empty;

    [ObservableProperty]
    private string _receiptStoreAddressText = string.Empty;

    [ObservableProperty]
    private string _receiptStorePhoneText = string.Empty;

    [ObservableProperty]
    private string _receiptAbnText = string.Empty;

    [ObservableProperty]
    private string _receiptReturnPolicyText = string.Empty;

    [ObservableProperty]
    private string _receiptPrinterTestStatusMessage = string.Empty;

    public SettingsViewModel(
        ICardTerminalSetupService setupService,
        ILocalizationService? localization = null,
        Func<CancellationToken, Task>? downloadCatalogAsync = null,
        Func<CancellationToken, Task>? resetCatalogAsync = null,
        Func<Task>? reregisterDeviceAsync = null,
        Action? returnToPos = null,
        IReceiptPrinterSettingsStore? receiptPrinterSettingsStore = null,
        IReceiptPrintService? receiptPrintService = null)
    {
        _setupService = setupService;
        _localization = localization;
        _downloadCatalogAsync = downloadCatalogAsync;
        _resetCatalogAsync = resetCatalogAsync;
        _reregisterDeviceAsync = reregisterDeviceAsync;
        _returnToPos = returnToPos;
        _receiptPrinterSettingsStore = receiptPrinterSettingsStore;
        _receiptPrintService = receiptPrintService;
        if (_localization is not null)
        {
            _localization.CultureChanged += (_, _) => RaiseLocalizedProperties();
        }

        SelectDataMaintenanceCommand = new RelayCommand(() => SelectedCategory = SettingsCategory.DataMaintenance);
        SelectPaymentTerminalCommand = new RelayCommand(() => SelectedCategory = SettingsCategory.PaymentTerminal);
        SelectReceiptPrinterCommand = new RelayCommand(() => SelectedCategory = SettingsCategory.ReceiptPrinter);
        SelectDeviceRegistrationCommand = new RelayCommand(() => SelectedCategory = SettingsCategory.DeviceRegistration);
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        LoadLocationsCommand = new AsyncRelayCommand(LoadLocationsAsync, CanLoadLocations);
        LoadDevicesCommand = new AsyncRelayCommand(LoadDevicesAsync, CanLoadDevices);
        SaveSquareCommand = new AsyncRelayCommand(SaveSquareAsync, CanSaveSquare);
        LoadDeviceCodesCommand = new AsyncRelayCommand(LoadDeviceCodesAsync, CanLoadDeviceCodes);
        CreateDeviceCodeCommand = new AsyncRelayCommand(CreateDeviceCodeAsync, CanCreateDeviceCode);
        RefreshDeviceCodeStatusCommand = new AsyncRelayCommand(RefreshDeviceCodeStatusAsync, CanRefreshDeviceCodeStatus);
        PairLinklyCloudCommand = new AsyncRelayCommand(PairLinklyCloudAsync, CanPairLinklyCloud);
        TestLinklyCommand = new AsyncRelayCommand(TestLinklyAsync, CanTestLinkly);
        SaveLinklyCommand = new AsyncRelayCommand(SaveLinklyAsync, CanSaveLinkly);
        SaveReceiptPrinterCommand = new AsyncRelayCommand(SaveReceiptPrinterAsync, CanSaveReceiptPrinter);
        TestReceiptPrinterCommand = new AsyncRelayCommand(TestReceiptPrinterAsync, CanTestReceiptPrinter);
        DownloadCatalogCommand = new AsyncRelayCommand(DownloadCatalogAsync, CanDownloadCatalog);
        ResetCatalogCommand = new AsyncRelayCommand(ResetCatalogAsync, CanResetCatalog);
        ReregisterDeviceCommand = new AsyncRelayCommand(ReregisterDeviceAsync, CanReregisterDevice);
        BackCommand = new RelayCommand(ReturnToPos, () => _returnToPos is not null);
        RefreshLocalizedMessages();
    }

    public ObservableCollection<SquareLocationOption> SquareLocations { get; } = [];

    public ObservableCollection<SquareDeviceOption> SquareDevices { get; } = [];

    public ObservableCollection<SquareDeviceCodeOption> SquareDeviceCodes { get; } = [];

    public IAsyncRelayCommand LoadCommand { get; }

    public IAsyncRelayCommand LoadLocationsCommand { get; }

    public IAsyncRelayCommand LoadDevicesCommand { get; }

    public IAsyncRelayCommand SaveSquareCommand { get; }

    public IAsyncRelayCommand LoadDeviceCodesCommand { get; }

    public IAsyncRelayCommand CreateDeviceCodeCommand { get; }

    public IAsyncRelayCommand RefreshDeviceCodeStatusCommand { get; }

    public IAsyncRelayCommand PairLinklyCloudCommand { get; }

    public IAsyncRelayCommand TestLinklyCommand { get; }

    public IAsyncRelayCommand SaveLinklyCommand { get; }

    public IRelayCommand SelectDataMaintenanceCommand { get; }

    public IRelayCommand SelectPaymentTerminalCommand { get; }

    public IRelayCommand SelectReceiptPrinterCommand { get; }

    public IRelayCommand SelectDeviceRegistrationCommand { get; }

    public IAsyncRelayCommand SaveReceiptPrinterCommand { get; }

    public IAsyncRelayCommand TestReceiptPrinterCommand { get; }

    public IAsyncRelayCommand DownloadCatalogCommand { get; }

    public IAsyncRelayCommand ResetCatalogCommand { get; }

    public IAsyncRelayCommand ReregisterDeviceCommand { get; }

    public IRelayCommand BackCommand { get; }

    public string ScreenTitleText => T("settings.title");

    public string SettingsSubtitleText => SelectedCategory switch
    {
        SettingsCategory.DataMaintenance => T("settings.subtitle.dataMaintenance"),
        SettingsCategory.PaymentTerminal => T("settings.subtitle.paymentTerminal"),
        SettingsCategory.ReceiptPrinter => T("settings.subtitle.receiptPrinter"),
        SettingsCategory.DeviceRegistration => T("settings.subtitle.deviceRegistration"),
        _ => T("settings.title")
    };

    public string CardTerminalTitleText => T("settings.subtitle.paymentTerminal");

    public string DataMaintenanceTitleText => T("settings.category.dataMaintenance");

    public string DeviceRegistrationTitleText => T("settings.category.deviceRegistration");

    public string SquareTitleText => T("settings.square.title");

    public string LinklyTitleText => T("settings.linkly.title");

    public string ReceiptPrinterTitleText => T("settings.receiptPrinter.title");

    public bool IsDataMaintenanceSelected => SelectedCategory == SettingsCategory.DataMaintenance;

    public bool IsPaymentTerminalSelected => SelectedCategory == SettingsCategory.PaymentTerminal;

    public bool IsReceiptPrinterSelected => SelectedCategory == SettingsCategory.ReceiptPrinter;

    public bool IsDeviceRegistrationSelected => SelectedCategory == SettingsCategory.DeviceRegistration;

    public string SquareTokenStatusText => HasSavedSquareToken
        ? T("settings.square.tokenStatus.cached")
        : T("settings.square.tokenStatus.missing");

    public bool IsSquareDeviceCodesSupported => !IsSandbox;

    public bool IsSquareDeviceCodesUnsupported => !IsSquareDeviceCodesSupported;

    public string SquareDeviceCodesUnavailableText => T("settings.square.deviceCodes.unsupported");

    public bool IsLinklyLocalMode => !IsLinklyCloudMode;

    public string LinklyCloudSecretStatusText => HasSavedLinklyCloudSecret
        ? T("settings.linkly.cloud.secretStatus.cached")
        : T("settings.linkly.cloud.secretStatus.missing");

    public CardTerminalEnvironment SelectedEnvironment => IsSandbox
        ? CardTerminalEnvironment.Sandbox
        : CardTerminalEnvironment.Production;

    public async Task LoadAsync()
    {
        await RunBusyAsync(async () =>
        {
            _loadedConfiguration = await _setupService.LoadConfigurationAsync();
            await LoadReceiptPrinterSettingsAsync();
            IsSandbox = _loadedConfiguration.Environment == CardTerminalEnvironment.Sandbox;
            LinklyHostText = _loadedConfiguration.LinklyHost;
            LinklyPortText = _loadedConfiguration.LinklyPort.ToString();
            IsLinklyCloudMode = _loadedConfiguration.LinklyConnectionMode == LinklyConnectionMode.Cloud;
            HasSavedLinklyCloudSecret = _loadedConfiguration.HasProtectedLinklyCloudSecret;
            LinklyPairCodeText = string.Empty;
            TimeoutSecondsText = _loadedConfiguration.TerminalTimeoutSeconds.ToString();
            HasSavedSquareToken = _loadedConfiguration.HasProtectedSquareAccessToken;
            _savedSquareLocationId = _loadedConfiguration.SquareLocationId;
            _savedSquareDeviceId = _loadedConfiguration.SquareDeviceId;
            _devicesLoadedForLocationId = null;
            LinklyConnectionSucceeded = false;
            ClearLinklyTestStatus();
            SquareLocations.Clear();
            SquareDevices.Clear();
            ResetSquareDeviceCodes();
            SelectedSquareLocation = null;
            SelectedSquareDevice = null;
            LogSquareSettings(
                $"load settings succeeded environment={SelectedEnvironment} hasSavedToken={HasSavedSquareToken} savedLocationId={LogValue(_savedSquareLocationId)} savedDeviceId={LogValue(_savedSquareDeviceId)}");
            SetStatus("settings.status.loaded");
        }, operationName: "load settings");
    }

    private async Task LoadLocationsAsync()
    {
        LogSquareSettings($"load locations requested environment={SelectedEnvironment}");
        await RunBusyAsync(async () =>
        {
            SquareLocations.ReplaceWith(await _setupService.ListSquareLocationsAsync(
                accessToken: null,
                SelectedEnvironment));
            SquareDevices.Clear();
            ResetSquareDeviceCodes();
            _devicesLoadedForLocationId = null;
            SelectedSquareDevice = null;
            SelectedSquareLocation = SquareLocations.FirstOrDefault(location =>
                string.Equals(location.Id, _savedSquareLocationId, StringComparison.OrdinalIgnoreCase));
            HasSavedSquareToken = true;
            LogSquareSettings(
                $"load locations succeeded environment={SelectedEnvironment} count={SquareLocations.Count} selectedLocationId={LogValue(SelectedSquareLocation?.Id)}");
            SetStatus(
                SquareLocations.Count == 0 ? "settings.status.noSquareLocations" : "settings.status.squareLocationsLoaded",
                SquareLocations.Count);
        }, operationName: "load square locations");
    }

    private async Task LoadDevicesAsync()
    {
        if (SelectedSquareLocation is null)
        {
            SetStatus("settings.status.selectSquareLocation");
            return;
        }

        LogSquareSettings($"load devices requested environment={SelectedEnvironment} locationId={LogValue(SelectedSquareLocation.Id)}");
        await RunBusyAsync(async () =>
        {
            await LoadSquareDevicesForLocationAsync(SelectedSquareLocation.Id, selectSavedDevice: true);
            HasSavedSquareToken = true;
            LogSquareSettings(
                $"load devices succeeded environment={SelectedEnvironment} locationId={LogValue(SelectedSquareLocation.Id)} count={SquareDevices.Count} selectedDeviceId={LogValue(SelectedSquareDevice?.Id)}");
            SetStatus(
                SquareDevices.Count == 0 ? "settings.status.noSquareDevices" : "settings.status.squareDevicesLoaded",
                SquareDevices.Count);
        }, operationName: "load square devices");
    }

    private async Task SaveSquareAsync()
    {
        if (SelectedSquareLocation is null)
        {
            SetStatus("settings.status.selectSquareLocation");
            return;
        }

        if (SelectedSquareDevice is null)
        {
            SetStatus("settings.status.selectSquareDevice");
            return;
        }

        if (!SquareLocations.Any(location => string.Equals(location.Id, SelectedSquareLocation.Id, StringComparison.OrdinalIgnoreCase)) ||
            !SquareDevices.Any(device => SquareDeviceIdNormalizer.AreEquivalent(device.Id, SelectedSquareDevice.Id)) ||
            !string.Equals(_devicesLoadedForLocationId, SelectedSquareLocation.Id, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("settings.status.loadSquareBeforeSave");
            return;
        }

        LogSquareSettings(
            $"save square requested environment={SelectedEnvironment} locationId={LogValue(SelectedSquareLocation.Id)} deviceId={LogValue(SelectedSquareDevice.Id)}");
        await RunBusyAsync(async () =>
        {
            var savedDeviceId = SquareDeviceIdNormalizer.NormalizeForTerminalCheckout(SelectedSquareDevice.Id);
            var configuration = new CardTerminalConfiguration(
                CardProcessorKind.Square,
                SelectedEnvironment,
                NormalizeHost(LinklyHostText),
                ParsePort(LinklyPortText),
                SelectedSquareLocation.Id,
                savedDeviceId,
                HasSavedSquareToken,
                ParseTimeoutSeconds(TimeoutSecondsText));

            await _setupService.SaveSquareAsync(
                configuration,
                squareAccessToken: null);
            _loadedConfiguration = configuration;
            _savedSquareLocationId = configuration.SquareLocationId;
            _savedSquareDeviceId = configuration.SquareDeviceId;
            HasSavedSquareToken = configuration.HasProtectedSquareAccessToken;
            LogSquareSettings(
                $"save square succeeded environment={SelectedEnvironment} locationId={LogValue(configuration.SquareLocationId)} selectedDeviceId={LogValue(SelectedSquareDevice.Id)} savedDeviceId={LogValue(configuration.SquareDeviceId)}");
            SetStatus("settings.status.squareSaved", SelectedSquareDevice.Name);
        }, operationName: "save square settings");
    }

    private async Task LoadDeviceCodesAsync()
    {
        if (SelectedSquareLocation is null)
        {
            SetStatus("settings.status.selectSquareLocation");
            return;
        }

        LogSquareSettings($"load device codes requested environment={SelectedEnvironment} locationId={LogValue(SelectedSquareLocation.Id)}");
        await RunBusyAsync(async () =>
        {
            SquareDeviceCodes.ReplaceWith(await _setupService.ListSquareDeviceCodesAsync(
                accessToken: null,
                SelectedEnvironment,
                SelectedSquareLocation.Id));
            SelectedSquareDeviceCode = SquareDeviceCodes.FirstOrDefault(deviceCode =>
                SquareDeviceIdNormalizer.AreEquivalent(deviceCode.DeviceId, _savedSquareDeviceId));
            HasSavedSquareToken = true;
            SuggestSquareDeviceCodeName(force: false);
            LogSquareSettings(
                $"load device codes succeeded environment={SelectedEnvironment} locationId={LogValue(SelectedSquareLocation.Id)} count={SquareDeviceCodes.Count} selectedDeviceCodeId={LogValue(SelectedSquareDeviceCode?.Id)}");
            SetStatus("settings.status.squareDeviceCodesLoaded", SquareDeviceCodes.Count);
        }, operationName: "load square device codes");
    }

    private async Task CreateDeviceCodeAsync()
    {
        if (SelectedSquareLocation is null)
        {
            SetStatus("settings.status.selectSquareLocation");
            return;
        }

        if (string.IsNullOrWhiteSpace(SquareDeviceCodeNameText))
        {
            SetStatus("settings.status.squareDeviceCodeNameRequired");
            return;
        }

        LogSquareSettings(
            $"create device code requested environment={SelectedEnvironment} locationId={LogValue(SelectedSquareLocation.Id)} name={LogValue(SquareDeviceCodeNameText.Trim())}");
        await RunBusyAsync(async () =>
        {
            var created = await _setupService.CreateSquareDeviceCodeAsync(
                accessToken: null,
                SelectedEnvironment,
                SelectedSquareLocation.Id,
                SquareDeviceCodeNameText);
            SquareDeviceCodes.Insert(0, created);
            SelectedSquareDeviceCode = created;
            HasSavedSquareToken = true;
            LogSquareSettings(
                $"create device code succeeded environment={SelectedEnvironment} locationId={LogValue(SelectedSquareLocation.Id)} deviceCodeId={created.Id} status={created.Status}");
            SetStatus("settings.status.squareDeviceCodeCreated", created.Code, created.Name);
        }, operationName: "create square device code");
    }

    private async Task RefreshDeviceCodeStatusAsync()
    {
        if (SelectedSquareDeviceCode is null)
        {
            SetStatus("settings.status.selectSquareDeviceCode");
            return;
        }

        LogSquareSettings(
            $"refresh device code requested environment={SelectedEnvironment} deviceCodeId={LogValue(SelectedSquareDeviceCode.Id)}");
        await RunBusyAsync(async () =>
        {
            var refreshed = await _setupService.GetSquareDeviceCodeAsync(
                accessToken: null,
                SelectedEnvironment,
                SelectedSquareDeviceCode.Id);
            ReplaceSquareDeviceCode(refreshed);
            SelectedSquareDeviceCode = refreshed;
            HasSavedSquareToken = true;

            if (string.Equals(refreshed.Status, "PAIRED", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(refreshed.DeviceId) &&
                SelectedSquareLocation is not null)
            {
                await LoadSquareDevicesForLocationAsync(SelectedSquareLocation.Id, selectSavedDevice: false);
                SelectedSquareDevice = SquareDevices.FirstOrDefault(device =>
                    SquareDeviceIdNormalizer.AreEquivalent(device.Id, refreshed.DeviceId));

                if (SelectedSquareDevice is not null)
                {
                    LogSquareSettings(
                        $"refresh device code paired environment={SelectedEnvironment} deviceCodeId={refreshed.Id} squareDeviceId={LogValue(refreshed.DeviceId)} selectedDeviceId={LogValue(SelectedSquareDevice.Id)}");
                    SetStatus("settings.status.squareDeviceCodePaired", SelectedSquareDevice.Name);
                    return;
                }
            }

            LogSquareSettings(
                $"refresh device code completed environment={SelectedEnvironment} deviceCodeId={refreshed.Id} status={refreshed.Status} squareDeviceId={LogValue(refreshed.DeviceId)}");
            SetStatus("settings.status.squareDeviceCodeNotPaired", refreshed.Status);
        }, operationName: "refresh square device code");
    }

    private async Task TestLinklyAsync()
    {
        await RunBusyAsync(async () =>
        {
            LinklyConnectionSucceeded = false;
            ClearLinklyTestStatus();
            var result = IsLinklyCloudMode
                ? await _setupService.TestLinklyCloudConnectionAsync(SelectedEnvironment)
                : await _setupService.TestLinklyConnectionAsync(
                    NormalizeHost(LinklyHostText),
                    ParsePort(LinklyPortText),
                    TimeSpan.FromSeconds(ParseTimeoutSeconds(TimeoutSecondsText)));
            LinklyConnectionSucceeded = result.Succeeded;

            if (string.IsNullOrWhiteSpace(result.Message))
            {
                var key = result.Succeeded
                    ? "settings.status.linklyTestSuccess"
                    : "settings.status.linklyTestFailed";
                SetLinklyTestStatus(key);
                SetStatus(key);
            }
            else
            {
                SetLinklyTestStatusOverride(result.Message);
                SetStatusOverride(result.Message);
            }
        });
    }

    private async Task PairLinklyCloudAsync()
    {
        await RunBusyAsync(async () =>
        {
            LinklyConnectionSucceeded = false;
            ClearLinklyTestStatus();
            var result = await _setupService.PairLinklyCloudAsync(
                SelectedEnvironment,
                LinklyPairCodeText);
            HasSavedLinklyCloudSecret = result.Succeeded || HasSavedLinklyCloudSecret;
            if (string.IsNullOrWhiteSpace(result.Message))
            {
                var key = result.Succeeded
                    ? "settings.status.linklyCloudPaired"
                    : "settings.status.linklyCloudPairFailed";
                SetLinklyTestStatus(key);
                SetStatus(key);
            }
            else
            {
                SetLinklyTestStatusOverride(result.Message);
                SetStatusOverride(result.Message);
            }
        });
    }

    private async Task SaveLinklyAsync()
    {
        if (!LinklyConnectionSucceeded)
        {
            SetStatus("settings.status.testLinklyBeforeSave");
            return;
        }

        await RunBusyAsync(async () =>
        {
            var configuration = _loadedConfiguration with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = IsLinklyCloudMode ? LinklyConnectionMode.Cloud : LinklyConnectionMode.Local,
                LinklyHost = NormalizeHost(LinklyHostText),
                LinklyPort = ParsePort(LinklyPortText),
                TerminalTimeoutSeconds = ParseTimeoutSeconds(TimeoutSecondsText),
                HasProtectedLinklyCloudSecret = HasSavedLinklyCloudSecret
            };

            if (IsLinklyCloudMode)
            {
                await _setupService.SaveLinklyCloudAsync(configuration);
            }
            else
            {
                await _setupService.SaveLinklyAsync(configuration);
            }

            _loadedConfiguration = configuration;
            SetStatus("settings.status.linklySaved");
        });
    }

    private async Task SaveReceiptPrinterAsync()
    {
        if (_receiptPrinterSettingsStore is null)
        {
            SetStatus("settings.status.receiptPrinterNotConfigured");
            return;
        }

        await RunBusyAsync(async () =>
        {
            var settings = CreateReceiptPrinterSettingsFromFields();
            await _receiptPrinterSettingsStore.SaveAsync(settings);
            ApplyReceiptPrinterSettings(settings);
            SetStatus("settings.status.receiptPrinterSaved");
        });
    }

    private async Task TestReceiptPrinterAsync()
    {
        if (_receiptPrintService is null)
        {
            SetStatus("settings.status.receiptPrinterNotConfigured");
            return;
        }

        await RunBusyAsync(async () =>
        {
            ReceiptPrinterTestStatusMessage = string.Empty;
            if (_receiptPrinterSettingsStore is not null)
            {
                await _receiptPrinterSettingsStore.SaveAsync(CreateReceiptPrinterSettingsFromFields());
            }

            var result = await _receiptPrintService.TestPrinterAsync();
            ReceiptPrinterTestStatusMessage = result.Message;
            _receiptPrinterTestStatusOverride = result.Message;
            SetStatusOverride(result.Message);
        });
    }

    private async Task DownloadCatalogAsync(CancellationToken cancellationToken)
    {
        if (_downloadCatalogAsync is null)
        {
            SetStatus("settings.status.catalogDownloadNotConfigured");
            return;
        }

        await RunBusyAsync(async () =>
        {
            SetStatus("settings.status.catalogDownloading");
            await _downloadCatalogAsync(cancellationToken);
            SetStatus("settings.status.catalogDownloadCompleted");
        });
    }

    private async Task ResetCatalogAsync(CancellationToken cancellationToken)
    {
        if (_resetCatalogAsync is null)
        {
            SetStatus("settings.status.catalogResetNotConfigured");
            return;
        }

        await RunBusyAsync(async () =>
        {
            SetStatus("settings.status.catalogResetting");
            await _resetCatalogAsync(cancellationToken);
            SetStatus("settings.status.catalogResetCompleted");
        });
    }

    private async Task ReregisterDeviceAsync()
    {
        if (_reregisterDeviceAsync is null)
        {
            SetStatus("settings.status.reregisterNotConfigured");
            return;
        }

        await RunBusyAsync(async () =>
        {
            SetStatus("settings.status.reregisterStarting");
            await _reregisterDeviceAsync();
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

    private bool CanLoadDeviceCodes()
    {
        return !IsBusy && IsSquareDeviceCodesSupported && SelectedSquareLocation is not null;
    }

    private bool CanCreateDeviceCode()
    {
        return !IsBusy &&
            IsSquareDeviceCodesSupported &&
            SelectedSquareLocation is not null &&
            !string.IsNullOrWhiteSpace(SquareDeviceCodeNameText);
    }

    private bool CanRefreshDeviceCodeStatus()
    {
        return !IsBusy && IsSquareDeviceCodesSupported && SelectedSquareDeviceCode is not null;
    }

    private bool CanTestLinkly()
    {
        return !IsBusy && (!IsLinklyCloudMode || HasSavedLinklyCloudSecret);
    }

    private bool CanSaveLinkly()
    {
        return !IsBusy && LinklyConnectionSucceeded;
    }

    private bool CanPairLinklyCloud()
    {
        return !IsBusy && IsLinklyCloudMode && !string.IsNullOrWhiteSpace(LinklyPairCodeText);
    }

    private bool CanSaveReceiptPrinter()
    {
        return !IsBusy && _receiptPrinterSettingsStore is not null;
    }

    private bool CanTestReceiptPrinter()
    {
        return !IsBusy && _receiptPrintService is not null;
    }

    private bool CanDownloadCatalog()
    {
        return !IsBusy && _downloadCatalogAsync is not null;
    }

    private bool CanResetCatalog()
    {
        return !IsBusy && _resetCatalogAsync is not null;
    }

    private bool CanReregisterDevice()
    {
        return !IsBusy && _reregisterDeviceAsync is not null;
    }

    private void ReturnToPos()
    {
        _returnToPos?.Invoke();
    }

    private async Task RunBusyAsync(Func<Task> action, string? operationName = null)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(operationName))
            {
                LogSquareSettings($"{operationName} canceled");
            }
            SetStatus("settings.status.operationCanceled");
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(operationName))
            {
                LogSquareSettings($"{operationName} failed message={LogValue(ex.Message)}");
            }
            SetStatusOverride(ex.Message);
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
        OnPropertyChanged(nameof(SettingsSubtitleText));
        OnPropertyChanged(nameof(DataMaintenanceTitleText));
        OnPropertyChanged(nameof(DeviceRegistrationTitleText));
        OnPropertyChanged(nameof(SquareTitleText));
        OnPropertyChanged(nameof(LinklyTitleText));
        OnPropertyChanged(nameof(LinklyCloudSecretStatusText));
        OnPropertyChanged(nameof(ReceiptPrinterTitleText));
        OnPropertyChanged(nameof(SquareTokenStatusText));
        OnPropertyChanged(nameof(SquareDeviceCodesUnavailableText));
        RefreshLocalizedMessages();
    }

    partial void OnSelectedCategoryChanged(SettingsCategory value)
    {
        OnPropertyChanged(nameof(SettingsSubtitleText));
        OnPropertyChanged(nameof(IsDataMaintenanceSelected));
        OnPropertyChanged(nameof(IsPaymentTerminalSelected));
        OnPropertyChanged(nameof(IsReceiptPrinterSelected));
        OnPropertyChanged(nameof(IsDeviceRegistrationSelected));
    }

    private async Task LoadReceiptPrinterSettingsAsync()
    {
        if (_receiptPrinterSettingsStore is null)
        {
            ApplyReceiptPrinterSettings(ReceiptPrinterSettings.Default);
            return;
        }

        ApplyReceiptPrinterSettings(await _receiptPrinterSettingsStore.LoadAsync());
    }

    private void ApplyReceiptPrinterSettings(ReceiptPrinterSettings settings)
    {
        ReceiptPrinterPortText = settings.PrinterPort;
        ReceiptBrandNameText = settings.BrandName;
        ReceiptStoreNameText = settings.StoreName;
        ReceiptStoreAddressText = settings.StoreAddress;
        ReceiptStorePhoneText = settings.StorePhone;
        ReceiptAbnText = settings.Abn;
        ReceiptReturnPolicyText = settings.ReturnPolicy;
    }

    private ReceiptPrinterSettings CreateReceiptPrinterSettingsFromFields()
    {
        return new ReceiptPrinterSettings(
            ReceiptPrinterPortText,
            ReceiptBrandNameText,
            ReceiptStoreNameText,
            ReceiptStoreAddressText,
            ReceiptStorePhoneText,
            ReceiptAbnText,
            ReceiptReturnPolicyText,
            ReceiptPrinterSettings.Default.CutDistance);
    }

    partial void OnIsSandboxChanged(bool value)
    {
        LogSquareSettings($"environment changed environment={SelectedEnvironment}");
        SquareLocations.Clear();
        SquareDevices.Clear();
        ResetSquareDeviceCodes();
        ResetLinklyConnectionTest();
        HasSavedLinklyCloudSecret = false;
        _devicesLoadedForLocationId = null;
        SelectedSquareLocation = null;
        SelectedSquareDevice = null;
        _ = RefreshLinklyCloudSecretStatusAsync(SelectedEnvironment);
        RaiseCommandStates();
        OnPropertyChanged(nameof(SelectedEnvironment));
        OnPropertyChanged(nameof(IsSquareDeviceCodesSupported));
        OnPropertyChanged(nameof(IsSquareDeviceCodesUnsupported));
        OnPropertyChanged(nameof(SquareDeviceCodesUnavailableText));
    }

    partial void OnHasSavedSquareTokenChanged(bool value)
    {
        RaiseCommandStates();
        OnPropertyChanged(nameof(SquareTokenStatusText));
    }

    partial void OnIsLinklyCloudModeChanged(bool value)
    {
        ResetLinklyConnectionTest();
        RaiseCommandStates();
        OnPropertyChanged(nameof(IsLinklyLocalMode));
    }

    partial void OnLinklyPairCodeTextChanged(string value)
    {
        RaiseCommandStates();
    }

    partial void OnHasSavedLinklyCloudSecretChanged(bool value)
    {
        RaiseCommandStates();
        OnPropertyChanged(nameof(LinklyCloudSecretStatusText));
    }

    private async Task RefreshLinklyCloudSecretStatusAsync(CardTerminalEnvironment environment)
    {
        var version = Interlocked.Increment(ref _linklySecretStatusVersion);
        try
        {
            var hasSecret = await _setupService.HasLinklyCloudSecretAsync(environment);
            if (version == _linklySecretStatusVersion && SelectedEnvironment == environment)
            {
                HasSavedLinklyCloudSecret = hasSecret;
            }
        }
        catch (Exception ex)
        {
            LogSquareSettings($"refresh linkly cloud secret status failed environment={environment} message={LogValue(ex.Message)}");
        }
    }

    partial void OnSelectedSquareLocationChanged(SquareLocationOption? value)
    {
        LogSquareSettings($"selected location changed locationId={LogValue(value?.Id)}");
        if (!string.Equals(_devicesLoadedForLocationId, value?.Id, StringComparison.OrdinalIgnoreCase))
        {
            SquareDevices.Clear();
            SelectedSquareDevice = null;
            _devicesLoadedForLocationId = null;
        }

        ResetSquareDeviceCodes();
        RaiseCommandStates();
    }

    partial void OnSelectedSquareDeviceChanged(SquareDeviceOption? value)
    {
        SuggestSquareDeviceCodeName(force: false);
        LogSquareSettings($"selected device changed deviceId={LogValue(value?.Id)}");
        if (!IsBusy &&
            value is not null &&
            SelectedSquareLocation is not null &&
            string.Equals(_devicesLoadedForLocationId, SelectedSquareLocation.Id, StringComparison.OrdinalIgnoreCase) &&
            !SquareDeviceIdNormalizer.AreEquivalent(value.Id, _savedSquareDeviceId))
        {
            SetStatus("settings.status.squareDeviceSwitchPendingSave", value.Name);
        }

        RaiseCommandStates();
    }

    partial void OnSelectedSquareDeviceCodeChanged(SquareDeviceCodeOption? value)
    {
        LogSquareSettings($"selected device code changed deviceCodeId={LogValue(value?.Id)} status={LogValue(value?.Status)}");
        RaiseCommandStates();
    }

    partial void OnSquareDeviceCodeNameTextChanged(string value)
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
        LoadDeviceCodesCommand.NotifyCanExecuteChanged();
        CreateDeviceCodeCommand.NotifyCanExecuteChanged();
        RefreshDeviceCodeStatusCommand.NotifyCanExecuteChanged();
        PairLinklyCloudCommand.NotifyCanExecuteChanged();
        TestLinklyCommand.NotifyCanExecuteChanged();
        SaveLinklyCommand.NotifyCanExecuteChanged();
        SaveReceiptPrinterCommand.NotifyCanExecuteChanged();
        TestReceiptPrinterCommand.NotifyCanExecuteChanged();
        DownloadCatalogCommand.NotifyCanExecuteChanged();
        ResetCatalogCommand.NotifyCanExecuteChanged();
        ReregisterDeviceCommand.NotifyCanExecuteChanged();
    }

    private void ResetLinklyConnectionTest()
    {
        LinklyConnectionSucceeded = false;
        ClearLinklyTestStatus();
    }

    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        _statusOverride = null;
        StatusMessage = Format(key, args);
    }

    private void SetStatusOverride(string statusText)
    {
        _statusOverride = statusText;
        StatusMessage = statusText;
    }

    private void SetLinklyTestStatus(string key, params object[] args)
    {
        _linklyTestStatusKey = key;
        _linklyTestStatusArgs = args;
        _linklyTestStatusOverride = null;
        LinklyTestStatusMessage = Format(key, args);
    }

    private void SetLinklyTestStatusOverride(string statusText)
    {
        _linklyTestStatusOverride = statusText;
        LinklyTestStatusMessage = statusText;
    }

    private void ClearLinklyTestStatus()
    {
        _linklyTestStatusKey = null;
        _linklyTestStatusArgs = [];
        _linklyTestStatusOverride = null;
        LinklyTestStatusMessage = string.Empty;
    }

    private void RefreshLocalizedMessages()
    {
        StatusMessage = _statusOverride ?? Format(_statusKey, _statusArgs);
        LinklyTestStatusMessage = _linklyTestStatusOverride
            ?? (_linklyTestStatusKey is null ? string.Empty : Format(_linklyTestStatusKey, _linklyTestStatusArgs));
        if (_receiptPrinterTestStatusOverride is not null)
        {
            ReceiptPrinterTestStatusMessage = _receiptPrinterTestStatusOverride;
        }
    }

    private string T(string key)
    {
        return _localization?.T(key) ?? LocalizationResourceProvider.Instance[key];
    }

    private string Format(string key, params object[] args)
    {
        var template = T(key);
        if (args.Length == 0)
        {
            return template;
        }

        var culture = _localization?.CurrentCulture ?? System.Globalization.CultureInfo.CurrentCulture;
        return string.Format(culture, template, args);
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

    private async Task LoadSquareDevicesForLocationAsync(string locationId, bool selectSavedDevice)
    {
        SquareDevices.ReplaceWith(await _setupService.ListSquareDevicesAsync(
            accessToken: null,
            SelectedEnvironment,
            locationId));
        _devicesLoadedForLocationId = locationId;
        SelectedSquareDevice = selectSavedDevice
            ? SquareDevices.FirstOrDefault(device =>
                SquareDeviceIdNormalizer.AreEquivalent(device.Id, _savedSquareDeviceId))
            : SelectedSquareDevice is not null
                ? SquareDevices.FirstOrDefault(device =>
                    SquareDeviceIdNormalizer.AreEquivalent(device.Id, SelectedSquareDevice.Id))
                : null;
    }

    private void ResetSquareDeviceCodes()
    {
        SquareDeviceCodes.Clear();
        SelectedSquareDeviceCode = null;
        SuggestSquareDeviceCodeName(force: true);
    }

    private void ReplaceSquareDeviceCode(SquareDeviceCodeOption updated)
    {
        for (var index = 0; index < SquareDeviceCodes.Count; index++)
        {
            if (string.Equals(SquareDeviceCodes[index].Id, updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                SquareDeviceCodes[index] = updated;
                return;
            }
        }

        SquareDeviceCodes.Insert(0, updated);
    }

    private void SuggestSquareDeviceCodeName(bool force)
    {
        var suggestion = SelectedSquareDevice?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(suggestion))
        {
            suggestion = DefaultSquareDeviceCodeName;
        }

        if (force ||
            string.IsNullOrWhiteSpace(SquareDeviceCodeNameText) ||
            string.Equals(SquareDeviceCodeNameText, _lastSquareDeviceCodeNameSuggestion, StringComparison.Ordinal))
        {
            SquareDeviceCodeNameText = suggestion;
        }

        _lastSquareDeviceCodeNameSuggestion = suggestion;
    }

    private static void LogSquareSettings(string message)
    {
        ConsoleLog.Write("Square", $"settings ui {message}");
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value;
    }
}
