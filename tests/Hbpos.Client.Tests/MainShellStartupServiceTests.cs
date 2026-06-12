using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Tests;

public sealed class MainShellStartupServiceTests
{
    private static readonly PosSessionState StartupSession = new(
        "HB POS",
        "DEFAULT",
        "Default Store",
        "Terminal 04",
        "C001",
        "Alice",
        false,
        0);

    [Fact]
    public async Task EvaluateAsync_WithAuthorizedCachedDevice_AllowsOfflineStartupWithoutApi()
    {
        var authorizationState = new DeviceAuthorizationState();
        var repository = new FakeLocalDeviceRepository
        {
            Latest = CreateAllowedDevice("1042")
        };
        var service = new MainShellStartupService(
            repository,
            new FakeDeviceFingerprintService("HW-001"),
            authorizationState);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.False(result.RequiresDeviceRegistration);
        Assert.Equal("1042", result.Session.StoreCode);
        Assert.Equal("POS-001", result.Session.DeviceCode);
        Assert.Equal(1, repository.GetLatestCallCount);
        Assert.NotNull(authorizationState.Current);
        Assert.Equal("AUTH-001", authorizationState.Current.AuthorizationCode);
    }

    [Theory]
    [MemberData(nameof(InvalidCachedDevices))]
    public async Task EvaluateAsync_WithMissingOrInvalidCachedDevice_RequiresRegistration(LocalDeviceCache? cachedDevice)
    {
        var authorizationState = new DeviceAuthorizationState();
        var service = new MainShellStartupService(
            new FakeLocalDeviceRepository { Latest = cachedDevice },
            new FakeDeviceFingerprintService("HW-001"),
            authorizationState);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.True(result.RequiresDeviceRegistration);
        Assert.Same(cachedDevice, result.CachedDevice);
        Assert.Null(authorizationState.Current);
    }

    public static IEnumerable<object?[]> InvalidCachedDevices()
    {
        yield return [null];
        yield return [CreateAllowedDevice("1042") with { IsAllowed = false }];
        yield return [CreateAllowedDevice("1042") with { AuthorizationCode = null }];
        yield return [CreateAllowedDevice("1042") with { HardwareId = "HW-OTHER" }];
    }

    private static LocalDeviceCache CreateAllowedDevice(string storeCode)
    {
        return new LocalDeviceCache(
            "POS-001",
            storeCode,
            "Main Store",
            "HW-001",
            1,
            true,
            null,
            DateTimeOffset.UtcNow,
            "AUTH-001");
    }

    private sealed class FakeLocalDeviceRepository : ILocalDeviceRepository
    {
        public LocalDeviceCache? Latest { get; init; }

        public int GetLatestCallCount { get; private set; }

        public Task<LocalDeviceCache?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            GetLatestCallCount++;
            return Task.FromResult(Latest);
        }

        public Task SaveAsync(DeviceRegisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("启动评估不应写入设备缓存。");
        }

        public Task SaveAsync(DeviceVerifyResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("启动评估不应写入设备缓存。");
        }

        public Task SaveAsync(DeviceReregisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("启动评估不应写入设备缓存。");
        }
    }

    private sealed class FakeDeviceFingerprintService(string hardwareId) : IDeviceFingerprintService
    {
        public string GetHardwareId() => hardwareId;
    }
}
