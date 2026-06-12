using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Tests;

public sealed class DeviceRegistrationWorkflowServiceTests
{
    [Fact]
    public async Task LoadStoresAsync_WithCachedDevice_SelectsCachedStoreAndPendingStatus()
    {
        var api = new FakeDeviceApiClient
        {
            Stores =
            [
                new StoreSelectionItem("1002", "Lutwyche", true),
                new StoreSelectionItem("1003", "Zillmere", true)
            ]
        };
        var service = new DeviceRegistrationWorkflowService(api, new FakeLocalDeviceRepository(), new FakeFingerprintService("HW-001"));
        var cached = new LocalDeviceCache("POS-001", "1003", "Zillmere", "HW-001", -1, false, null, DateTimeOffset.UtcNow);

        var result = await service.LoadStoresAsync(cached, isReregisterMode: false);

        Assert.Equal("POS-001", result.DeviceCode);
        Assert.True(result.HasPendingRegistration);
        Assert.Equal("Device registration is pending approval.", result.StatusMessage);
        Assert.Equal("1003", result.SelectedStore?.StoreCode);
        Assert.Equal(2, result.Stores.Count);
    }

    [Fact]
    public async Task LoadStoresAsync_WithRejectedCachedDevice_DoesNotMapToPendingStatus()
    {
        var api = new FakeDeviceApiClient
        {
            Stores = [new StoreSelectionItem("1003", "Zillmere", true)]
        };
        var service = new DeviceRegistrationWorkflowService(api, new FakeLocalDeviceRepository(), new FakeFingerprintService("HW-001"));
        var cached = new LocalDeviceCache("POS-OLD", "1002", "Lutwyche", "HW-001", 1, false, "Device hardware is already registered to another store.", DateTimeOffset.UtcNow);

        var result = await service.LoadStoresAsync(cached, isReregisterMode: false);

        Assert.False(result.HasPendingRegistration);
        Assert.Equal("POS-OLD", result.DeviceCode);
        Assert.Equal("1003", result.SelectedStore?.StoreCode);
    }

    [Fact]
    public async Task LoadStoresAsync_ReregisterMode_HidesCurrentAndInactiveStores()
    {
        var api = new FakeDeviceApiClient
        {
            Stores =
            [
                new StoreSelectionItem("1002", "Current", true),
                new StoreSelectionItem("1003", "Inactive Target", false),
                new StoreSelectionItem("1004", "Active Target", true)
            ]
        };
        var service = new DeviceRegistrationWorkflowService(api, new FakeLocalDeviceRepository(), new FakeFingerprintService("HW-001"));

        var result = await service.LoadStoresAsync(
            cachedDevice: null,
            isReregisterMode: true,
            excludedStoreCode: "1002");

        var store = Assert.Single(result.Stores);
        Assert.Equal("1004", store.StoreCode);
        Assert.Equal("1004", result.SelectedStore?.StoreCode);
    }

    [Fact]
    public async Task RegisterAsync_SavesResponseAndReturnsPendingResult()
    {
        var api = new FakeDeviceApiClient
        {
            RegisterResponse = new DeviceRegisterResponse("POS-001", "1002", "Lutwyche", -1, false, "Pending approval")
        };
        var repository = new FakeLocalDeviceRepository();
        var service = new DeviceRegistrationWorkflowService(api, repository, new FakeFingerprintService("HW-001"));

        var result = await service.RegisterAsync(new StoreSelectionItem("1002", "Lutwyche", true), "HW-001");

        Assert.Equal("1002", api.LastRegisterRequest?.StoreCode);
        Assert.Equal("HW-001", api.LastRegisterRequest?.HardwareId);
        Assert.NotNull(repository.LastRegisterResponse);
        Assert.Equal("HW-001", repository.LastHardwareId);
        Assert.Equal("POS-001", result.DeviceCode);
        Assert.True(result.HasPendingRegistration);
        Assert.Equal("Pending approval", result.StatusMessage);
        Assert.False(result.ShouldRaiseActivated);
    }

    [Fact]
    public async Task RegisterAsync_WhenResponseIsRejected_DoesNotMapToPendingRegistration()
    {
        var api = new FakeDeviceApiClient
        {
            RegisterResponse = new DeviceRegisterResponse(
                "POS-OLD",
                "1002",
                "Lutwyche",
                1,
                false,
                "Device hardware is already registered to another store.")
        };
        var repository = new FakeLocalDeviceRepository();
        var service = new DeviceRegistrationWorkflowService(api, repository, new FakeFingerprintService("HW-001"));

        var result = await service.RegisterAsync(new StoreSelectionItem("1003", "Zillmere", true), "HW-001");

        Assert.False(result.HasPendingRegistration);
        Assert.False(result.ShouldRaiseActivated);
        Assert.Equal("Device hardware is already registered to another store.", result.StatusMessage);
        Assert.Null(repository.LastRegisterResponse);
    }

    [Fact]
    public async Task VerifyAsync_WhenAuthorizationCodeIsMissing_ReturnsVerifyAgainMessage()
    {
        var api = new FakeDeviceApiClient
        {
            VerifyResponse = new DeviceVerifyResponse("POS-001", "1002", "Lutwyche", 1, true, "Device is enabled.", null)
        };
        var repository = new FakeLocalDeviceRepository();
        var service = new DeviceRegistrationWorkflowService(api, repository, new FakeFingerprintService("HW-001"));

        var result = await service.VerifyAsync(new StoreSelectionItem("1002", "Lutwyche", true), "POS-001", "HW-001");

        Assert.NotNull(repository.LastVerifyResponse);
        Assert.Equal("POS-001", api.LastVerifyRequest?.DeviceCode);
        Assert.Equal("1002", api.LastVerifyRequest?.StoreCode);
        Assert.False(result.HasPendingRegistration);
        Assert.Equal("Device authorization code was not returned. Please verify again.", result.StatusMessage);
        Assert.False(result.ShouldRaiseActivated);
    }

    [Fact]
    public async Task ReregisterAsync_SavesOnlyAcceptedPendingResponses()
    {
        var api = new FakeDeviceApiClient
        {
            ReregisterResponse = new DeviceReregisterResponse("POS-NEW", "1003", "Zillmere", -1, false, "Pending approval")
        };
        var repository = new FakeLocalDeviceRepository();
        var service = new DeviceRegistrationWorkflowService(api, repository, new FakeFingerprintService("HW-001"));

        var accepted = await service.ReregisterAsync(new StoreSelectionItem("1003", "Zillmere", true), "HW-001");

        Assert.NotNull(repository.LastReregisterResponse);
        Assert.True(accepted.ShouldRaiseReregistered);
        Assert.Equal("Pending approval", accepted.StatusMessage);

        repository.Reset();
        api.ReregisterResponse = new DeviceReregisterResponse("POS-NEW", "1003", "Zillmere", 1, true, "Device is enabled.", "AUTH-001");

        var allowed = await service.ReregisterAsync(new StoreSelectionItem("1003", "Zillmere", true), "HW-001");

        Assert.Null(repository.LastReregisterResponse);
        Assert.False(allowed.ShouldRaiseReregistered);
        Assert.True(allowed.ShouldRaiseActivated);
    }

    private sealed class FakeDeviceApiClient : IDeviceApiClient
    {
        public IReadOnlyList<StoreSelectionItem> Stores { get; init; } = [];

        public DeviceRegisterResponse? RegisterResponse { get; init; }

        public DeviceVerifyResponse? VerifyResponse { get; init; }

        public DeviceReregisterResponse? ReregisterResponse { get; set; }

        public DeviceRegisterRequest? LastRegisterRequest { get; private set; }

        public DeviceVerifyRequest? LastVerifyRequest { get; private set; }

        public Task<IReadOnlyList<StoreSelectionItem>> GetStoresAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<StoreSelectionItem>>(Stores);
        }

        public Task<DeviceRegisterResponse> RegisterAsync(
            DeviceRegisterRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRegisterRequest = request;
            return Task.FromResult(RegisterResponse!);
        }

        public Task<DeviceVerifyResponse> VerifyAsync(
            DeviceVerifyRequest request,
            CancellationToken cancellationToken = default)
        {
            LastVerifyRequest = request;
            return Task.FromResult(VerifyResponse!);
        }

        public Task<DeviceReregisterResponse> ReregisterAsync(
            DeviceReregisterRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ReregisterResponse!);
        }
    }

    private sealed class FakeLocalDeviceRepository : ILocalDeviceRepository
    {
        public DeviceRegisterResponse? LastRegisterResponse { get; private set; }

        public DeviceVerifyResponse? LastVerifyResponse { get; private set; }

        public DeviceReregisterResponse? LastReregisterResponse { get; private set; }

        public string? LastHardwareId { get; private set; }

        public Task<LocalDeviceCache?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalDeviceCache?>(null);
        }

        public Task SaveAsync(DeviceRegisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            LastRegisterResponse = response;
            LastHardwareId = hardwareId;
            return Task.CompletedTask;
        }

        public Task SaveAsync(DeviceVerifyResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            LastVerifyResponse = response;
            LastHardwareId = hardwareId;
            return Task.CompletedTask;
        }

        public Task SaveAsync(DeviceReregisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            LastReregisterResponse = response;
            LastHardwareId = hardwareId;
            return Task.CompletedTask;
        }

        public void Reset()
        {
            LastRegisterResponse = null;
            LastVerifyResponse = null;
            LastReregisterResponse = null;
            LastHardwareId = null;
        }
    }

    private sealed class FakeFingerprintService(string hardwareId) : IDeviceFingerprintService
    {
        public string GetHardwareId() => hardwareId;
    }
}
