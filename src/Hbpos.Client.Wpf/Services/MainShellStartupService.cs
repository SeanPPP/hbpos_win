using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.Services;

public sealed record MainShellStartupResult(
    PosSessionState Session,
    bool RequiresDeviceRegistration,
    LocalDeviceCache? CachedDevice);

public interface IMainShellStartupService
{
    Task<MainShellStartupResult> EvaluateAsync(
        PosSessionState session,
        bool previewMode,
        CancellationToken cancellationToken = default);

    void SetAuthorizedDevice(
        string deviceCode,
        string storeCode,
        string hardwareId,
        string authorizationCode);

    void ClearAuthorization();
}

public sealed class MainShellStartupService(
    ILocalDeviceRepository deviceRepository,
    IDeviceFingerprintService fingerprintService,
    DeviceAuthorizationState deviceAuthorizationState) : IMainShellStartupService
{
    public async Task<MainShellStartupResult> EvaluateAsync(
        PosSessionState session,
        bool previewMode,
        CancellationToken cancellationToken = default)
    {
        if (previewMode)
        {
            deviceAuthorizationState.Clear();
            return new MainShellStartupResult(session, false, null);
        }

        var cachedDevice = await deviceRepository.GetLatestAsync(cancellationToken);
        var hardwareId = fingerprintService.GetHardwareId();
        if (cachedDevice is null ||
            !cachedDevice.IsAllowed ||
            string.IsNullOrWhiteSpace(cachedDevice.AuthorizationCode) ||
            !string.Equals(cachedDevice.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase))
        {
            deviceAuthorizationState.Clear();
            return new MainShellStartupResult(session, true, cachedDevice);
        }

        SetAuthorizedDevice(
            cachedDevice.DeviceCode,
            cachedDevice.StoreCode,
            cachedDevice.HardwareId,
            cachedDevice.AuthorizationCode);

        return new MainShellStartupResult(
            session with
            {
                StoreCode = cachedDevice.StoreCode,
                StoreName = cachedDevice.StoreName,
                DeviceCode = cachedDevice.DeviceCode
            },
            false,
            cachedDevice);
    }

    public void SetAuthorizedDevice(
        string deviceCode,
        string storeCode,
        string hardwareId,
        string authorizationCode)
    {
        deviceAuthorizationState.Set(new DeviceAuthorizationContext(
            deviceCode,
            storeCode,
            hardwareId,
            authorizationCode));
    }

    public void ClearAuthorization()
    {
        deviceAuthorizationState.Clear();
    }
}
