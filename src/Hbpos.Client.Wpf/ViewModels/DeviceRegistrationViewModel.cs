using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class DeviceRegistrationViewModel : ObservableObject
{
    private readonly IDeviceApiClient _deviceApiClient;
    private readonly ILocalDeviceRepository _deviceRepository;
    private readonly IDeviceFingerprintService _fingerprintService;

    [ObservableProperty]
    private StoreSelectionItem? _selectedStore;

    [ObservableProperty]
    private string _hardwareId = string.Empty;

    [ObservableProperty]
    private string _deviceCode = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasPendingRegistration;

    public DeviceRegistrationViewModel(
        IDeviceApiClient deviceApiClient,
        ILocalDeviceRepository deviceRepository,
        IDeviceFingerprintService fingerprintService)
    {
        _deviceApiClient = deviceApiClient;
        _deviceRepository = deviceRepository;
        _fingerprintService = fingerprintService;

        RegisterCommand = new AsyncRelayCommand(RegisterAsync, CanRegister);
        VerifyCommand = new AsyncRelayCommand(VerifyAsync, CanVerify);
    }

    public ObservableCollection<StoreSelectionItem> Stores { get; } = [];

    public IAsyncRelayCommand RegisterCommand { get; }

    public IAsyncRelayCommand VerifyCommand { get; }

    public event EventHandler<DeviceActivatedEventArgs>? DeviceActivated;

    public async Task InitializeAsync(LocalDeviceCache? cachedDevice, CancellationToken cancellationToken = default)
    {
        Prepare(cachedDevice);
        await LoadStoresAsync(cachedDevice, cancellationToken);
    }

    public void Prepare(LocalDeviceCache? cachedDevice)
    {
        HardwareId = _fingerprintService.GetHardwareId();
        Stores.Clear();
        SelectedStore = null;

        if (cachedDevice is not null)
        {
            DeviceCode = cachedDevice.DeviceCode;
            HasPendingRegistration = !cachedDevice.IsAllowed;
        }
        else
        {
            DeviceCode = string.Empty;
            HasPendingRegistration = false;
        }

        StatusMessage = "Loading stores...";
        NotifyCommandState();
    }

    public async Task LoadStoresAsync(LocalDeviceCache? cachedDevice, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusMessage = "Loading stores...";

        try
        {
            var stores = await _deviceApiClient.GetStoresAsync(cancellationToken);
            Stores.Clear();
            foreach (var store in stores)
            {
                Stores.Add(store);
            }

            if (cachedDevice is not null)
            {
                DeviceCode = cachedDevice.DeviceCode;
                HasPendingRegistration = !cachedDevice.IsAllowed;
                StatusMessage = cachedDevice.Message ?? "Device registration is pending approval.";
                SelectedStore = Stores.FirstOrDefault(x => string.Equals(x.StoreCode, cachedDevice.StoreCode, StringComparison.OrdinalIgnoreCase))
                    ?? Stores.FirstOrDefault();
            }
            else
            {
                SelectedStore = Stores.FirstOrDefault();
                StatusMessage = Stores.Count == 0
                    ? "No active stores are available."
                    : "Select a store and submit this register for approval.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        NotifyCommandState();
    }

    partial void OnSelectedStoreChanged(StoreSelectionItem? value)
    {
        NotifyCommandState();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyCommandState();
    }

    private async Task RegisterAsync()
    {
        if (SelectedStore is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "Submitting device registration...";
            var response = await _deviceApiClient.RegisterAsync(new DeviceRegisterRequest(
                SelectedStore.StoreCode,
                HardwareId,
                Environment.MachineName));
            await _deviceRepository.SaveAsync(response, HardwareId);
            ApplyRegisterResponse(response);
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

    private async Task VerifyAsync()
    {
        if (SelectedStore is null || string.IsNullOrWhiteSpace(DeviceCode))
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "Checking device approval...";
            var response = await _deviceApiClient.VerifyAsync(new DeviceVerifyRequest(
                DeviceCode,
                SelectedStore.StoreCode,
                HardwareId,
                Environment.MachineName));
            await _deviceRepository.SaveAsync(response, HardwareId);
            ApplyVerifyResponse(response);
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

    private void ApplyRegisterResponse(DeviceRegisterResponse response)
    {
        DeviceCode = response.DeviceCode;
        HasPendingRegistration = !response.IsAllowed;
        StatusMessage = response.Message ?? (response.IsAllowed ? "Device is enabled." : "Device registration is pending approval.");
        if (response.IsAllowed)
        {
            if (string.IsNullOrWhiteSpace(response.AuthorizationCode))
            {
                StatusMessage = "Device authorization code was not returned. Please verify again.";
                return;
            }

            DeviceActivated?.Invoke(
                this,
                new DeviceActivatedEventArgs(response.DeviceCode, response.StoreCode, response.StoreName, HardwareId, response.AuthorizationCode));
        }
    }

    private void ApplyVerifyResponse(DeviceVerifyResponse response)
    {
        HasPendingRegistration = !response.IsAllowed;
        StatusMessage = response.Message ?? (response.IsAllowed ? "Device is enabled." : "Device registration is pending approval.");
        if (response.IsAllowed)
        {
            if (string.IsNullOrWhiteSpace(response.AuthorizationCode))
            {
                StatusMessage = "Device authorization code was not returned. Please verify again.";
                return;
            }

            DeviceActivated?.Invoke(
                this,
                new DeviceActivatedEventArgs(response.DeviceCode, response.StoreCode, response.StoreName, HardwareId, response.AuthorizationCode));
        }
    }

    private bool CanRegister()
    {
        return !IsBusy && SelectedStore is not null;
    }

    private bool CanVerify()
    {
        return !IsBusy && SelectedStore is not null && !string.IsNullOrWhiteSpace(DeviceCode);
    }

    private void NotifyCommandState()
    {
        RegisterCommand.NotifyCanExecuteChanged();
        VerifyCommand.NotifyCanExecuteChanged();
    }
}

public sealed record DeviceActivatedEventArgs(
    string DeviceCode,
    string StoreCode,
    string StoreName,
    string HardwareId,
    string AuthorizationCode = "");
