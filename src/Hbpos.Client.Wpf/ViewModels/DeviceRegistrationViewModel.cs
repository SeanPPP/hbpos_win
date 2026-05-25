using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class DeviceRegistrationViewModel : ObservableObject
{
    private readonly IDeviceRegistrationWorkflowService _workflowService;
    private string? _excludedStoreCode;

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

    [ObservableProperty]
    private bool _isReregisterMode;

    [ObservableProperty]
    private bool _canCancel;

    public DeviceRegistrationViewModel(
        IDeviceApiClient deviceApiClient,
        ILocalDeviceRepository deviceRepository,
        IDeviceFingerprintService fingerprintService)
        : this(new DeviceRegistrationWorkflowService(deviceApiClient, deviceRepository, fingerprintService))
    {
    }

    public DeviceRegistrationViewModel(IDeviceRegistrationWorkflowService workflowService)
    {
        _workflowService = workflowService;

        RegisterCommand = new AsyncRelayCommand(RegisterAsync, CanRegister);
        VerifyCommand = new AsyncRelayCommand(VerifyAsync, CanVerify);
        CancelCommand = new RelayCommand(Cancel, () => CanCancel && !IsBusy);
    }

    public ObservableCollection<StoreSelectionItem> Stores { get; } = [];

    public IAsyncRelayCommand RegisterCommand { get; }

    public IAsyncRelayCommand VerifyCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public string TitleText => IsReregisterMode ? "重新注册设备" : "设备注册";

    public string RegisterButtonText => IsReregisterMode ? "提交重新注册" : "提交申请";

    public event EventHandler<DeviceActivatedEventArgs>? DeviceActivated;

    public event EventHandler<DeviceReregisteredEventArgs>? DeviceReregistered;

    public event EventHandler? CancelRequested;

    public async Task InitializeAsync(LocalDeviceCache? cachedDevice, CancellationToken cancellationToken = default)
    {
        Prepare(cachedDevice);
        await LoadStoresAsync(cachedDevice, cancellationToken);
    }

    public void Prepare(LocalDeviceCache? cachedDevice)
    {
        IsReregisterMode = false;
        CanCancel = false;
        _excludedStoreCode = null;
        HardwareId = _workflowService.GetHardwareId();
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

        StatusMessage = DeviceRegistrationWorkflowService.LoadingStoresMessage;
        NotifyCommandState();
    }

    public void PrepareReregister(string currentStoreCode)
    {
        IsReregisterMode = true;
        CanCancel = true;
        _excludedStoreCode = currentStoreCode;
        HardwareId = _workflowService.GetHardwareId();
        Stores.Clear();
        SelectedStore = null;
        DeviceCode = string.Empty;
        HasPendingRegistration = false;
        StatusMessage = DeviceRegistrationWorkflowService.LoadingStoresMessage;
        NotifyCommandState();
    }

    public async Task LoadStoresAsync(LocalDeviceCache? cachedDevice, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusMessage = DeviceRegistrationWorkflowService.LoadingStoresMessage;

        try
        {
            var result = await _workflowService.LoadStoresAsync(cachedDevice, IsReregisterMode, _excludedStoreCode, cancellationToken);
            ApplyLoadResult(result);
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

    partial void OnIsReregisterModeChanged(bool value)
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(RegisterButtonText));
    }

    partial void OnSelectedStoreChanged(StoreSelectionItem? value)
    {
        NotifyCommandState();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyCommandState();
    }

    partial void OnCanCancelChanged(bool value)
    {
        NotifyCommandState();
    }

    private async Task RegisterAsync()
    {
        if (IsReregisterMode)
        {
            await ReregisterAsync();
            return;
        }

        await RegisterDeviceAsync();
    }

    private async Task RegisterDeviceAsync()
    {
        if (SelectedStore is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "Submitting device registration...";
            var result = await _workflowService.RegisterAsync(SelectedStore, HardwareId);
            ApplyActionResult(result);
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

    private async Task ReregisterAsync()
    {
        if (SelectedStore is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "Submitting device reregistration...";
            var result = await _workflowService.ReregisterAsync(SelectedStore, HardwareId);
            ApplyActionResult(result);
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
            var result = await _workflowService.VerifyAsync(SelectedStore, DeviceCode, HardwareId);
            ApplyActionResult(result);
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

    private void ApplyLoadResult(DeviceRegistrationLoadResult result)
    {
        Stores.Clear();
        foreach (var store in result.Stores)
        {
            Stores.Add(store);
        }

        DeviceCode = result.DeviceCode;
        HasPendingRegistration = result.HasPendingRegistration;
        StatusMessage = result.StatusMessage;
        SelectedStore = result.SelectedStore;
        NotifyCommandState();
    }

    private void ApplyActionResult(DeviceRegistrationActionResult result)
    {
        DeviceCode = result.DeviceCode;
        HasPendingRegistration = result.HasPendingRegistration;
        StatusMessage = result.StatusMessage;

        if (result.ShouldRaiseReregistered)
        {
            IsReregisterMode = false;
            CanCancel = false;
            DeviceReregistered?.Invoke(
                this,
                new DeviceReregisteredEventArgs(result.DeviceCode, result.StoreCode, result.StoreName, result.HardwareId));
        }

        if (result.ShouldRaiseActivated)
        {
            DeviceActivated?.Invoke(
                this,
                new DeviceActivatedEventArgs(result.DeviceCode, result.StoreCode, result.StoreName, result.HardwareId, result.AuthorizationCode ?? string.Empty));
        }

        NotifyCommandState();
    }

    private bool CanRegister()
    {
        return !IsBusy && SelectedStore is not null && !HasPendingRegistration;
    }

    private bool CanVerify()
    {
        return !IsBusy && SelectedStore is not null && !string.IsNullOrWhiteSpace(DeviceCode);
    }

    private void NotifyCommandState()
    {
        RegisterCommand.NotifyCanExecuteChanged();
        VerifyCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void Cancel()
    {
        if (CanCancel && !IsBusy)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed record DeviceActivatedEventArgs(
    string DeviceCode,
    string StoreCode,
    string StoreName,
    string HardwareId,
    string AuthorizationCode = "");

public sealed record DeviceReregisteredEventArgs(
    string DeviceCode,
    string StoreCode,
    string StoreName,
    string HardwareId);
