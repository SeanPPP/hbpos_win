using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Wpf.Services;

public interface IDeviceRegistrationWorkflowService
{
    string GetHardwareId();

    Task<DeviceRegistrationLoadResult> LoadStoresAsync(
        LocalDeviceCache? cachedDevice,
        bool isReregisterMode,
        string? excludedStoreCode = null,
        CancellationToken cancellationToken = default);

    Task<DeviceRegistrationActionResult> RegisterAsync(
        StoreSelectionItem selectedStore,
        string hardwareId,
        CancellationToken cancellationToken = default);

    Task<DeviceRegistrationActionResult> VerifyAsync(
        StoreSelectionItem selectedStore,
        string deviceCode,
        string hardwareId,
        CancellationToken cancellationToken = default);

    Task<DeviceRegistrationActionResult> ReregisterAsync(
        StoreSelectionItem selectedStore,
        string hardwareId,
        CancellationToken cancellationToken = default);
}

public sealed record DeviceRegistrationLoadResult(
    IReadOnlyList<StoreSelectionItem> Stores,
    StoreSelectionItem? SelectedStore,
    string DeviceCode,
    bool HasPendingRegistration,
    string StatusMessage);

public sealed record DeviceRegistrationActionResult(
    string DeviceCode,
    string StoreCode,
    string StoreName,
    string HardwareId,
    bool HasPendingRegistration,
    string StatusMessage,
    string? AuthorizationCode,
    bool ShouldRaiseActivated,
    bool ShouldRaiseReregistered);

public sealed class DeviceRegistrationWorkflowService(
    IDeviceApiClient deviceApiClient,
    ILocalDeviceRepository deviceRepository,
    IDeviceFingerprintService fingerprintService) : IDeviceRegistrationWorkflowService
{
    private const int PendingDeviceStatus = -1;
    private const string LoadingStoresStatus = "Loading stores...";
    private const string PendingApprovalStatus = "Device registration is pending approval.";
    private const string EnabledStatus = "Device is enabled.";
    private const string MissingAuthorizationStatus = "Device authorization code was not returned. Please verify again.";
    private const string NoStoresStatus = "No active stores are available.";
    private const string SelectStoreStatus = "Select a store and submit this register for approval.";
    private const string NoReregisterStoresStatus = "No other active stores are available.";
    private const string SelectReregisterStoreStatus = "Select a new store and submit device reregistration.";

    public string GetHardwareId()
    {
        return fingerprintService.GetHardwareId();
    }

    public async Task<DeviceRegistrationLoadResult> LoadStoresAsync(
        LocalDeviceCache? cachedDevice,
        bool isReregisterMode,
        string? excludedStoreCode = null,
        CancellationToken cancellationToken = default)
    {
        var stores = await deviceApiClient.GetStoresAsync(cancellationToken);
        var visibleStores = stores
            .Where(store => CanShowStore(store, excludedStoreCode))
            .ToArray();

        if (cachedDevice is not null)
        {
            return new DeviceRegistrationLoadResult(
                visibleStores,
                visibleStores.FirstOrDefault(x => string.Equals(x.StoreCode, cachedDevice.StoreCode, StringComparison.OrdinalIgnoreCase))
                    ?? visibleStores.FirstOrDefault(),
                cachedDevice.DeviceCode,
                cachedDevice.DeviceStatus == PendingDeviceStatus,
                cachedDevice.Message ?? PendingApprovalStatus);
        }

        return new DeviceRegistrationLoadResult(
            visibleStores,
            visibleStores.FirstOrDefault(),
            string.Empty,
            false,
            isReregisterMode
                ? visibleStores.Length == 0 ? NoReregisterStoresStatus : SelectReregisterStoreStatus
                : visibleStores.Length == 0 ? NoStoresStatus : SelectStoreStatus);
    }

    public async Task<DeviceRegistrationActionResult> RegisterAsync(
        StoreSelectionItem selectedStore,
        string hardwareId,
        CancellationToken cancellationToken = default)
    {
        var response = await deviceApiClient.RegisterAsync(
            new DeviceRegisterRequest(selectedStore.StoreCode, hardwareId, Environment.MachineName),
            cancellationToken);
        if (ShouldSaveRegisterResponse(response))
        {
            await deviceRepository.SaveAsync(response, hardwareId, cancellationToken);
        }

        return CreateActionResult(
            response.DeviceCode,
            response.StoreCode,
            response.StoreName,
            response.DeviceStatus,
            response.IsAllowed,
            response.Message,
            response.AuthorizationCode,
            hardwareId,
            shouldRaiseReregistered: false);
    }

    public async Task<DeviceRegistrationActionResult> VerifyAsync(
        StoreSelectionItem selectedStore,
        string deviceCode,
        string hardwareId,
        CancellationToken cancellationToken = default)
    {
        var response = await deviceApiClient.VerifyAsync(
            new DeviceVerifyRequest(deviceCode, selectedStore.StoreCode, hardwareId, Environment.MachineName),
            cancellationToken);
        await deviceRepository.SaveAsync(response, hardwareId, cancellationToken);

        return CreateActionResult(
            response.DeviceCode,
            response.StoreCode,
            response.StoreName,
            response.DeviceStatus,
            response.IsAllowed,
            response.Message,
            response.AuthorizationCode,
            hardwareId,
            shouldRaiseReregistered: false);
    }

    public async Task<DeviceRegistrationActionResult> ReregisterAsync(
        StoreSelectionItem selectedStore,
        string hardwareId,
        CancellationToken cancellationToken = default)
    {
        var response = await deviceApiClient.ReregisterAsync(
            new DeviceReregisterRequest(selectedStore.StoreCode, hardwareId, Environment.MachineName),
            cancellationToken);
        var shouldRaiseReregistered = IsAcceptedReregister(response);
        if (shouldRaiseReregistered)
        {
            await deviceRepository.SaveAsync(response, hardwareId, cancellationToken);
        }

        return CreateActionResult(
            response.DeviceCode,
            response.StoreCode,
            response.StoreName,
            response.DeviceStatus,
            response.IsAllowed,
            response.Message,
            response.AuthorizationCode,
            hardwareId,
            shouldRaiseReregistered);
    }

    internal static string LoadingStoresMessage => LoadingStoresStatus;

    private static DeviceRegistrationActionResult CreateActionResult(
        string deviceCode,
        string storeCode,
        string storeName,
        int deviceStatus,
        bool isAllowed,
        string? message,
        string? authorizationCode,
        string hardwareId,
        bool shouldRaiseReregistered)
    {
        var statusMessage = message ?? (isAllowed ? EnabledStatus : PendingApprovalStatus);
        var shouldRaiseActivated = false;

        if (isAllowed)
        {
            if (string.IsNullOrWhiteSpace(authorizationCode))
            {
                statusMessage = MissingAuthorizationStatus;
            }
            else
            {
                shouldRaiseActivated = true;
            }
        }

        return new DeviceRegistrationActionResult(
            deviceCode,
            storeCode,
            storeName,
            hardwareId,
            deviceStatus == PendingDeviceStatus,
            statusMessage,
            authorizationCode,
            shouldRaiseActivated,
            shouldRaiseReregistered);
    }

    private static bool IsAcceptedReregister(DeviceReregisterResponse response)
    {
        return response.DeviceStatus == PendingDeviceStatus
            && !string.IsNullOrWhiteSpace(response.DeviceCode)
            && !string.IsNullOrWhiteSpace(response.StoreCode);
    }

    private static bool ShouldSaveRegisterResponse(DeviceRegisterResponse response)
    {
        return response.DeviceStatus == PendingDeviceStatus
            || response.IsAllowed && !string.IsNullOrWhiteSpace(response.AuthorizationCode);
    }

    private static bool CanShowStore(StoreSelectionItem store, string? excludedStoreCode)
    {
        return string.IsNullOrWhiteSpace(excludedStoreCode)
            || !string.Equals(store.StoreCode, excludedStoreCode, StringComparison.OrdinalIgnoreCase);
    }
}
