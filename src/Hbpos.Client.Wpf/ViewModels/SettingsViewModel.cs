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

public enum LinklySettingsMode
{
    LocalIp,
    CloudDirectSync,
    CloudBackendAsync
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private const string DefaultSquareDeviceCodeName = "HBPOS Terminal";

    private readonly ICardTerminalSetupService _setupService;
    private readonly ILocalizationService? _localization;
    private readonly Func<CancellationToken, Task>? _downloadCatalogAsync;
    private readonly Func<CancellationToken, Task>? _resetCatalogAsync;
    private readonly Func<CancellationToken, Task>? _resetTestSalesDataAsync;
    private readonly Func<bool>? _confirmResetTestSalesData;
    private readonly Func<Task<DeviceReregistrationStartResult>>? _reregisterDeviceAsync;
    private readonly Action? _returnToPos;
    private readonly IReceiptPrinterSettingsStore? _receiptPrinterSettingsStore;
    private readonly IReceiptPrintService? _receiptPrintService;
    private readonly ICardRecoveryResultDialogService? _cardRecoveryResultDialogService;
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
    private int _linklyCredentialStatusVersion;
    private int _linklyCredentialEditVersion;

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
    private LinklySettingsMode _selectedLinklyMode = LinklySettingsMode.LocalIp;

    [ObservableProperty]
    private string _linklyPairCodeText = string.Empty;

    [ObservableProperty]
    private string _linklyCloudUsernameText = string.Empty;

    [ObservableProperty]
    private string _linklyCloudPasswordText = string.Empty;

    [ObservableProperty]
    private bool _hasSavedLinklyCloudPassword;

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
        Func<Task<DeviceReregistrationStartResult>>? reregisterDeviceAsync = null,
        Action? returnToPos = null,
        IReceiptPrinterSettingsStore? receiptPrinterSettingsStore = null,
        IReceiptPrintService? receiptPrintService = null,
        Func<CancellationToken, Task>? resetTestSalesDataAsync = null,
        Func<bool>? confirmResetTestSalesData = null,
        ICardRecoveryResultDialogService? cardRecoveryResultDialogService = null)
    {
        _setupService = setupService;
        _localization = localization;
        _downloadCatalogAsync = downloadCatalogAsync;
        _resetCatalogAsync = resetCatalogAsync;
        _resetTestSalesDataAsync = resetTestSalesDataAsync;
        _confirmResetTestSalesData = confirmResetTestSalesData;
        _reregisterDeviceAsync = reregisterDeviceAsync;
        _returnToPos = returnToPos;
        _receiptPrinterSettingsStore = receiptPrinterSettingsStore;
        _receiptPrintService = receiptPrintService;
        _cardRecoveryResultDialogService = cardRecoveryResultDialogService;
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
        SaveLinklyCloudCredentialCommand = new AsyncRelayCommand(SaveLinklyCloudCredentialAsync, CanSaveLinklyCloudCredential);
        CancelLinklyCloudPairingCommand = new RelayCommand(CancelLinklyCloudPairing, CanCancelLinklyCloudPairing);
        TestLinklyCommand = new AsyncRelayCommand(TestLinklyAsync, CanTestLinkly);
        SaveLinklyCommand = new AsyncRelayCommand(SaveLinklyAsync, CanSaveLinkly);
        SaveReceiptPrinterCommand = new AsyncRelayCommand(SaveReceiptPrinterAsync, CanSaveReceiptPrinter);
        TestReceiptPrinterCommand = new AsyncRelayCommand(TestReceiptPrinterAsync, CanTestReceiptPrinter);
        DownloadCatalogCommand = new AsyncRelayCommand(DownloadCatalogAsync, CanDownloadCatalog);
        ResetCatalogCommand = new AsyncRelayCommand(ResetCatalogAsync, CanResetCatalog);
        ResetTestSalesDataCommand = new AsyncRelayCommand(ResetTestSalesDataAsync, CanResetTestSalesData);
        ReregisterDeviceCommand = new AsyncRelayCommand(ReregisterDeviceAsync, CanReregisterDevice);
        TestLinklyTransactionStatusCommand = new AsyncRelayCommand(TestLinklyTransactionStatusAsync, CanTestLinklyTransactionStatus);
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

    public IAsyncRelayCommand SaveLinklyCloudCredentialCommand { get; }

    public IRelayCommand CancelLinklyCloudPairingCommand { get; }

    public IAsyncRelayCommand TestLinklyCommand { get; }

    public IAsyncRelayCommand TestLinklyTransactionStatusCommand { get; }

    public IAsyncRelayCommand SaveLinklyCommand { get; }

    public IRelayCommand SelectDataMaintenanceCommand { get; }

    public IRelayCommand SelectPaymentTerminalCommand { get; }

    public IRelayCommand SelectReceiptPrinterCommand { get; }

    public IRelayCommand SelectDeviceRegistrationCommand { get; }

    public IAsyncRelayCommand SaveReceiptPrinterCommand { get; }

    public IAsyncRelayCommand TestReceiptPrinterCommand { get; }

    public IAsyncRelayCommand DownloadCatalogCommand { get; }

    public IAsyncRelayCommand ResetCatalogCommand { get; }

    public IAsyncRelayCommand ResetTestSalesDataCommand { get; }

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

    public bool IsDebugTestSalesDataResetVisible
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    public string SquareTokenStatusText => HasSavedSquareToken
        ? T("settings.square.tokenStatus.cached")
        : T("settings.square.tokenStatus.missing");

    public bool IsSquareDeviceCodesSupported => !IsSandbox;

    public bool IsSquareDeviceCodesUnsupported => !IsSquareDeviceCodesSupported;

    public string SquareDeviceCodesUnavailableText => T("settings.square.deviceCodes.unsupported");

    public bool CanChangeEnvironment => !IsBusy;

    public bool IsLinklyCloudMode
    {
        get => SelectedLinklyMode is LinklySettingsMode.CloudDirectSync or LinklySettingsMode.CloudBackendAsync;
        set => SelectedLinklyMode = value ? LinklySettingsMode.CloudDirectSync : LinklySettingsMode.LocalIp;
    }

    public bool IsLinklyLocalMode => IsLinklyLocalIpMode;

    public bool IsLinklyLocalIpMode
    {
        get => SelectedLinklyMode == LinklySettingsMode.LocalIp;
        set
        {
            if (value)
            {
                SelectedLinklyMode = LinklySettingsMode.LocalIp;
            }
        }
    }

    public bool IsLinklyCloudDirectSyncMode
    {
        get => SelectedLinklyMode == LinklySettingsMode.CloudDirectSync;
        set
        {
            if (value)
            {
                SelectedLinklyMode = LinklySettingsMode.CloudDirectSync;
            }
        }
    }

    public bool IsLinklyCloudBackendAsyncMode
    {
        get => SelectedLinklyMode == LinklySettingsMode.CloudBackendAsync;
        set
        {
            if (value)
            {
                SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync;
            }
        }
    }

    public bool IsLinklyStandardActionMode => !IsLinklyCloudBackendAsyncMode;

    public string LinklyCloudSecretStatusText => HasSavedLinklyCloudSecret
        ? T("settings.linkly.cloud.secretStatus.cached")
        : T("settings.linkly.cloud.secretStatus.missing");

    public string LinklyCloudCredentialStatusText => HasSavedLinklyCloudPassword
        ? T("settings.linkly.cloud.credentialStatus.cached")
        : T("settings.linkly.cloud.credentialStatus.missing");

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
            SelectedLinklyMode = ToSettingsMode(_loadedConfiguration.LinklyConnectionMode);
            await LoadLinklyCloudCredentialFieldsAsync(SelectedEnvironment);
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
            try
            {
                LinklyConnectionSucceeded = false;
                ClearLinklyTestStatus();
                LinklyConnectionTestResult result;
                if (IsLinklyCloudBackendAsyncMode)
                {
                    result = await _setupService.TestLinklyCloudBackendConnectionAsync(SelectedEnvironment);
                }
                else if (IsLinklyCloudDirectSyncMode)
                {
                    result = await _setupService.TestLinklyCloudConnectionAsync(SelectedEnvironment);
                }
                else
                {
                    result = await _setupService.TestLinklyConnectionAsync(
                        NormalizeHost(LinklyHostText),
                        ParsePort(LinklyPortText),
                        TimeSpan.FromSeconds(ParseTimeoutSeconds(TimeoutSecondsText)));
                }

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

            }
            catch (Exception ex)
            {
                SetLinklyTestStatusOverride(ex.Message);
                SetStatusOverride(ex.Message);
                throw;
            }
        });
    }

    private void ShowFailedLastTransactionDialogIfNeeded(LinklyConnectionTestResult result)
    {
        if (result.Succeeded ||
            result.StatusTest is not { } status ||
            !IsFailedLastTransactionStatus(status))
        {
            return;
        }

        _cardRecoveryResultDialogService?.Show(new CardRecoveryResultDialogViewModel(
            "上一笔刷卡交易未成功",
            "已完成不带 TxnRef 的 Transaction Status 查询，终端返回上一笔交易未成功。请按认证表记录 TxnRef、时间和响应码。",
            CardRecoveryResultSeverity.Warning,
            orderGuid: null,
            amount: null,
            sessionId: status.TransactionReference,
            txnRef: status.TxnRef,
            responseCode: status.ResponseCode,
            responseText: status.ResponseText ?? result.Message,
            timestamp: status.Timestamp ?? DateTimeOffset.Now));
    }

    private static bool IsFailedLastTransactionStatus(LinklyStatusTestDetails status)
    {
        var code = status.ResponseCode?.Trim();
        if (string.Equals(code, "00", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "T0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var text = status.ResponseText?.Trim();
        return !string.IsNullOrWhiteSpace(code) ||
            ContainsFailureText(text);
    }

    private static bool ContainsFailureText(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) &&
            (text.Contains("DECLINED", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("CANCELLED", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("FAILED", StringComparison.OrdinalIgnoreCase));
    }

    private async Task TestLinklyTransactionStatusAsync()
    {
        await RunBusyAsync(async () =>
        {
            try
            {
                ClearLinklyTestStatus();
                var result = await _setupService.TestLinklyCloudBackendTransactionStatusAsync(SelectedEnvironment);
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

                ShowFailedLastTransactionDialogIfNeeded(result);
            }
            catch (Exception ex)
            {
                SetLinklyTestStatusOverride(ex.Message);
                SetStatusOverride(ex.Message);
                throw;
            }
        });
    }

    private async Task PairLinklyCloudAsync()
    {
        var pairingEnvironment = SelectedEnvironment;
        var pairCode = LinklyPairCodeText;
        var username = LinklyCloudUsernameText;
        var password = LinklyCloudPasswordText;
        LogLinklyCloudSettings(
            $"pair clicked environment={pairingEnvironment} hasUsername={!string.IsNullOrWhiteSpace(username)} hasCurrentPassword={!string.IsNullOrWhiteSpace(password)} hasSavedPassword={HasSavedLinklyCloudPassword} hasPairCode={!string.IsNullOrWhiteSpace(pairCode)}");
        if (!ValidateLinklyCloudPairingInput())
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            try
            {
                LinklyConnectionSucceeded = false;
                ClearLinklyTestStatus();
                var result = await _setupService.PairLinklyCloudAsync(
                    pairingEnvironment,
                    pairCode,
                    username,
                    password,
                    syncBackendTerminalCredential: IsLinklyCloudBackendAsyncMode);
                LogLinklyCloudSettings($"pair completed environment={pairingEnvironment} currentEnvironment={SelectedEnvironment} success={result.Succeeded} hasMessage={!string.IsNullOrWhiteSpace(result.Message)}");
                if (SelectedEnvironment == pairingEnvironment)
                {
                    HasSavedLinklyCloudSecret = result.Succeeded || HasSavedLinklyCloudSecret;
                }

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
            }
            catch (Exception ex)
            {
                SetLinklyTestStatusOverride(ex.Message);
                SetStatusOverride(ex.Message);
                throw;
            }
        });
    }

    private bool ValidateLinklyCloudPairingInput()
    {
        if (string.IsNullOrWhiteSpace(LinklyCloudUsernameText))
        {
            LogLinklyCloudSettings($"pair validation blocked environment={SelectedEnvironment} reason=missing-username");
            SetLinklyTestStatus("settings.linklyCloud.usernameRequired");
            SetStatus("settings.linklyCloud.usernameRequired");
            return false;
        }

        if (string.IsNullOrWhiteSpace(LinklyCloudPasswordText) && !HasSavedLinklyCloudPassword)
        {
            LogLinklyCloudSettings($"pair validation blocked environment={SelectedEnvironment} reason=missing-password");
            SetLinklyTestStatus("settings.linklyCloud.passwordRequired");
            SetStatus("settings.linklyCloud.passwordRequired");
            return false;
        }

        if (string.IsNullOrWhiteSpace(LinklyPairCodeText))
        {
            LogLinklyCloudSettings($"pair validation blocked environment={SelectedEnvironment} reason=missing-pair-code");
            SetLinklyTestStatus("settings.linklyCloud.pairCodeRequired");
            SetStatus("settings.linklyCloud.pairCodeRequired");
            return false;
        }

        return true;
    }

    private async Task SaveLinklyCloudCredentialAsync()
    {
        var credentialEnvironment = SelectedEnvironment;
        var syncBackendCredential = IsLinklyCloudBackendAsyncMode;
        var username = LinklyCloudUsernameText;
        var password = LinklyCloudPasswordText;
        await RunBusyAsync(async () =>
        {
            try
            {
                LogLinklyCloudSettings($"save credential clicked environment={credentialEnvironment} hasUsername={!string.IsNullOrWhiteSpace(username)} hasPassword={!string.IsNullOrWhiteSpace(password)}");
                await _setupService.SaveLinklyCloudCredentialAsync(
                    credentialEnvironment,
                    username,
                    password,
                    syncBackendCredential: syncBackendCredential);
                if (SelectedEnvironment == credentialEnvironment)
                {
                    HasSavedLinklyCloudPassword = true;
                    LinklyCloudPasswordText = string.Empty;
                }

                LogLinklyCloudSettings($"save credential completed environment={credentialEnvironment} currentEnvironment={SelectedEnvironment} success=true");
                var statusKey = syncBackendCredential
                    ? "settings.status.linklyCloudCredentialSynced"
                    : "settings.status.linklyCloudCredentialSaved";
                SetLinklyTestStatus(statusKey);
                SetStatus(statusKey);
            }
            catch (Exception ex)
            {
                SetLinklyTestStatusOverride(ex.Message);
                SetStatusOverride(ex.Message);
                throw;
            }
        }, operationName: "save linkly cloud credential");
    }

    private void CancelLinklyCloudPairing()
    {
        // 取消配对只清空本次输入，不删除本机已保存的 Cloud API 测试账号。
        LogLinklyCloudSettings($"pair cancel clicked environment={SelectedEnvironment} hadPassword={!string.IsNullOrWhiteSpace(LinklyCloudPasswordText)} hadPairCode={!string.IsNullOrWhiteSpace(LinklyPairCodeText)}");
        LinklyPairCodeText = string.Empty;
        LinklyCloudPasswordText = string.Empty;
        ResetLinklyConnectionTest();
        RaiseCommandStates();
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
                Environment = SelectedEnvironment,
                LinklyConnectionMode = ToConnectionMode(SelectedLinklyMode),
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

    private async Task ResetTestSalesDataAsync(CancellationToken cancellationToken)
    {
#if DEBUG
        if (_resetTestSalesDataAsync is null)
        {
            SetStatus("settings.status.testSalesDataResetNotConfigured");
            return;
        }

        if (_confirmResetTestSalesData?.Invoke() != true)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            SetStatus("settings.status.testSalesDataResetting");
            await _resetTestSalesDataAsync(cancellationToken);
            SetStatus("settings.status.testSalesDataResetCompleted");
        });
#else
        await Task.CompletedTask;
        SetStatus("settings.status.testSalesDataResetNotConfigured");
#endif
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
            var result = await _reregisterDeviceAsync();
            // 设置页不切换屏幕，启动被拦截时需要把原因留在当前页状态栏。
            if (!result.Started && !string.IsNullOrWhiteSpace(result.StatusMessage))
            {
                SetStatusOverride(result.StatusMessage);
            }
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
        return !IsBusy && (!IsLinklyCloudDirectSyncMode || HasSavedLinklyCloudSecret);
    }

    private bool CanTestLinklyTransactionStatus()
    {
        return !IsBusy && IsLinklyCloudBackendAsyncMode;
    }

    private bool CanSaveLinkly()
    {
        return !IsBusy && LinklyConnectionSucceeded;
    }

    private bool CanPairLinklyCloud()
    {
        return !IsBusy && IsLinklyCloudMode;
    }

    private bool CanSaveLinklyCloudCredential()
    {
        return !IsBusy &&
            IsLinklyCloudMode &&
            !string.IsNullOrWhiteSpace(LinklyCloudUsernameText) &&
            !string.IsNullOrWhiteSpace(LinklyCloudPasswordText);
    }

    private bool CanCancelLinklyCloudPairing()
    {
        return !IsBusy &&
            IsLinklyCloudMode &&
            (!string.IsNullOrWhiteSpace(LinklyPairCodeText) ||
             !string.IsNullOrWhiteSpace(LinklyCloudPasswordText));
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

    private bool CanResetTestSalesData()
    {
#if DEBUG
        return !IsBusy && _resetTestSalesDataAsync is not null;
#else
        return false;
#endif
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
        OnPropertyChanged(nameof(LinklyCloudCredentialStatusText));
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
        // 切环境时先同步清空当前界面字段，避免旧环境数据在异步刷新完成前继续停留在界面上。
        ResetLinklyConnectionTest();
        LinklyCloudUsernameText = string.Empty;
        HasSavedLinklyCloudSecret = false;
        HasSavedLinklyCloudPassword = false;
        LinklyCloudPasswordText = string.Empty;
        LinklyPairCodeText = string.Empty;
        _devicesLoadedForLocationId = null;
        SelectedSquareLocation = null;
        SelectedSquareDevice = null;
        _ = RefreshLinklyCloudSecretStatusAsync(SelectedEnvironment);
        _ = RefreshLinklyCloudCredentialStatusAsync(SelectedEnvironment);
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

    partial void OnSelectedLinklyModeChanged(LinklySettingsMode value)
    {
        ResetLinklyConnectionTest();
        RaiseCommandStates();
        OnPropertyChanged(nameof(IsLinklyCloudMode));
        OnPropertyChanged(nameof(IsLinklyLocalMode));
        OnPropertyChanged(nameof(IsLinklyLocalIpMode));
        OnPropertyChanged(nameof(IsLinklyCloudDirectSyncMode));
        OnPropertyChanged(nameof(IsLinklyCloudBackendAsyncMode));
        OnPropertyChanged(nameof(IsLinklyStandardActionMode));
    }

    partial void OnLinklyCloudUsernameTextChanged(string value)
    {
        Interlocked.Increment(ref _linklyCredentialEditVersion);
        ResetLinklyConnectionTest();
        RaiseCommandStates();
    }

    partial void OnLinklyCloudPasswordTextChanged(string value)
    {
        Interlocked.Increment(ref _linklyCredentialEditVersion);
        ResetLinklyConnectionTest();
        RaiseCommandStates();
    }

    partial void OnHasSavedLinklyCloudPasswordChanged(bool value)
    {
        RaiseCommandStates();
        OnPropertyChanged(nameof(LinklyCloudCredentialStatusText));
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

    private static void ApplyLinklyCloudCredentialFields(
        LinklyCloudCredentialSettings credential,
        Action<string> setUsername,
        Action<string> setPassword,
        Action<bool> setHasSavedPassword)
    {
        setUsername(credential.Username ?? string.Empty);
        setPassword(string.Empty);
        setHasSavedPassword(credential.HasProtectedPassword);
    }

    private void ClearLinklyCloudCredentialFields()
    {
        LinklyCloudUsernameText = string.Empty;
        LinklyCloudPasswordText = string.Empty;
        HasSavedLinklyCloudPassword = false;
    }

    private async Task LoadLinklyCloudCredentialFieldsAsync(CardTerminalEnvironment environment)
    {
        try
        {
            var credential = await _setupService.LoadLinklyCloudCredentialAsync(environment);
            ApplyLinklyCloudCredentialFields(
                credential,
                username => LinklyCloudUsernameText = username,
                password => LinklyCloudPasswordText = password,
                hasSavedPassword => HasSavedLinklyCloudPassword = hasSavedPassword);
        }
        catch (Exception ex)
        {
            LogSquareSettings($"load linkly cloud credential failed environment={environment} message={LogValue(ex.Message)}");
            ClearLinklyCloudCredentialFields();
        }
    }

    private async Task RefreshLinklyCloudCredentialStatusAsync(CardTerminalEnvironment environment)
    {
        var version = Interlocked.Increment(ref _linklyCredentialStatusVersion);
        var editVersion = _linklyCredentialEditVersion;
        try
        {
            var credential = await _setupService.LoadLinklyCloudCredentialAsync(environment);
            // 只有最新刷新、环境仍匹配且用户没有继续编辑时才回写，避免异步结果覆盖正在输入的凭据。
            if (version == _linklyCredentialStatusVersion &&
                editVersion == _linklyCredentialEditVersion &&
                SelectedEnvironment == environment)
            {
                ApplyLinklyCloudCredentialFields(
                    credential,
                    username => LinklyCloudUsernameText = username,
                    password => LinklyCloudPasswordText = password,
                    hasSavedPassword => HasSavedLinklyCloudPassword = hasSavedPassword);
            }
        }
        catch (Exception ex)
        {
            LogSquareSettings($"refresh linkly cloud credential status failed environment={environment} message={LogValue(ex.Message)}");
            if (version == _linklyCredentialStatusVersion &&
                editVersion == _linklyCredentialEditVersion &&
                SelectedEnvironment == environment)
            {
                ClearLinklyCloudCredentialFields();
            }
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
        OnPropertyChanged(nameof(CanChangeEnvironment));
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
        SaveLinklyCloudCredentialCommand.NotifyCanExecuteChanged();
        CancelLinklyCloudPairingCommand.NotifyCanExecuteChanged();
        TestLinklyCommand.NotifyCanExecuteChanged();
        TestLinklyTransactionStatusCommand.NotifyCanExecuteChanged();
        SaveLinklyCommand.NotifyCanExecuteChanged();
        SaveReceiptPrinterCommand.NotifyCanExecuteChanged();
        TestReceiptPrinterCommand.NotifyCanExecuteChanged();
        DownloadCatalogCommand.NotifyCanExecuteChanged();
        ResetCatalogCommand.NotifyCanExecuteChanged();
        ResetTestSalesDataCommand.NotifyCanExecuteChanged();
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

    private static LinklySettingsMode ToSettingsMode(LinklyConnectionMode mode)
    {
        return CardTerminalSettings.NormalizeLinklyConnectionMode(mode) switch
        {
            LinklyConnectionMode.CloudDirectSync => LinklySettingsMode.CloudDirectSync,
            LinklyConnectionMode.CloudBackendAsync => LinklySettingsMode.CloudBackendAsync,
            _ => LinklySettingsMode.LocalIp
        };
    }

    private static LinklyConnectionMode ToConnectionMode(LinklySettingsMode mode)
    {
        return mode switch
        {
            LinklySettingsMode.CloudDirectSync => LinklyConnectionMode.CloudDirectSync,
            LinklySettingsMode.CloudBackendAsync => LinklyConnectionMode.CloudBackendAsync,
            _ => LinklyConnectionMode.LocalIp
        };
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

    private static void LogLinklyCloudSettings(string message)
    {
        LinklyJsonLog.WriteMessage("LinklyCloud", "settings-ui", $"settings ui {message}");
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value;
    }
}
