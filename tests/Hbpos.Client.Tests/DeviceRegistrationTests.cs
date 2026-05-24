using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Tests;

public sealed class DeviceRegistrationTests
{
    [Fact]
    public async Task LocalDeviceRepository_SavesAndRestoresPendingRegistration()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalDeviceRepository(store, new FakeAuthorizationProtector());
            await schema.InitializeAsync();

            await repository.SaveAsync(
                new DeviceRegisterResponse("POS-001", "1002", "Lutwyche", 1, true, "Device is enabled.", "AUTH-001"),
                "HW-001");

            var cached = await repository.GetLatestAsync();

            Assert.NotNull(cached);
            Assert.Equal("POS-001", cached.DeviceCode);
            Assert.Equal("1002", cached.StoreCode);
            Assert.Equal("Lutwyche", cached.StoreName);
            Assert.Equal("HW-001", cached.HardwareId);
            Assert.Equal(1, cached.DeviceStatus);
            Assert.True(cached.IsAllowed);
            Assert.Equal("Device is enabled.", cached.Message);
            Assert.Equal("AUTH-001", cached.AuthorizationCode);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task DeviceRegistrationViewModel_LoadsStoresSortedAndSubmitsPendingRegistration()
    {
        var api = new FakeDeviceApiClient
        {
            Stores =
            [
                new StoreSelectionItem("1003", "Zillmere", true),
                new StoreSelectionItem("1002", "Lutwyche", true)
            ],
            RegisterResponse = new DeviceRegisterResponse("POS-001", "1002", "Lutwyche", -1, false, "Pending approval")
        };
        var repository = new FakeLocalDeviceRepository();
        var viewModel = new DeviceRegistrationViewModel(
            api,
            repository,
            new FakeFingerprintService("HW-001"));

        await viewModel.InitializeAsync(cachedDevice: null);
        await viewModel.RegisterCommand.ExecuteAsync(null);

        Assert.Equal("Lutwyche (1002)", viewModel.SelectedStore?.DisplayName);
        Assert.Equal("POS-001", viewModel.DeviceCode);
        Assert.True(viewModel.HasPendingRegistration);
        Assert.Equal("Pending approval", viewModel.StatusMessage);
        Assert.Equal("HW-001", repository.LastHardwareId);
        Assert.NotNull(repository.LastRegisterResponse);
    }

    [Fact]
    public async Task DeviceRegistrationViewModel_VerifyRaisesActivationWhenDeviceIsAllowed()
    {
        var api = new FakeDeviceApiClient
        {
            Stores = [new StoreSelectionItem("1002", "Lutwyche", true)],
            VerifyResponse = new DeviceVerifyResponse("POS-001", "1002", "Lutwyche", 1, true, "Device is enabled.", "AUTH-001")
        };
        var repository = new FakeLocalDeviceRepository();
        var viewModel = new DeviceRegistrationViewModel(
            api,
            repository,
            new FakeFingerprintService("HW-001"));
        var cached = new LocalDeviceCache("POS-001", "1002", "Lutwyche", "HW-001", -1, false, "Pending approval", DateTimeOffset.UtcNow);
        DeviceActivatedEventArgs? activated = null;
        viewModel.DeviceActivated += (_, args) => activated = args;

        await viewModel.InitializeAsync(cached);
        await viewModel.VerifyCommand.ExecuteAsync(null);

        Assert.NotNull(activated);
        Assert.Equal("POS-001", activated.DeviceCode);
        Assert.Equal("1002", activated.StoreCode);
        Assert.Equal("Lutwyche", activated.StoreName);
        Assert.Equal("HW-001", activated.HardwareId);
        Assert.Equal("AUTH-001", activated.AuthorizationCode);
        Assert.NotNull(repository.LastVerifyResponse);
    }

    [Fact]
    public async Task DeviceRegistrationViewModel_ReregisterMode_SubmitsReregisterAndFiltersCurrentStore()
    {
        var api = new FakeDeviceApiClient
        {
            Stores =
            [
                new StoreSelectionItem("1002", "Lutwyche", true),
                new StoreSelectionItem("1003", "Zillmere", true)
            ],
            ReregisterResponse = new DeviceReregisterResponse("POS-NEW", "1003", "Zillmere", -1, false, "Pending approval")
        };
        var repository = new FakeLocalDeviceRepository();
        var viewModel = new DeviceRegistrationViewModel(
            api,
            repository,
            new FakeFingerprintService("HW-001"));
        DeviceReregisteredEventArgs? reregistered = null;
        viewModel.DeviceReregistered += (_, args) => reregistered = args;

        viewModel.PrepareReregister("1002");
        await viewModel.LoadStoresAsync(cachedDevice: null);
        await viewModel.RegisterCommand.ExecuteAsync(null);

        Assert.Equal("Zillmere (1003)", viewModel.SelectedStore?.DisplayName);
        Assert.Equal("POS-NEW", viewModel.DeviceCode);
        Assert.True(viewModel.HasPendingRegistration);
        Assert.Equal("Pending approval", viewModel.StatusMessage);
        Assert.NotNull(repository.LastReregisterResponse);
        Assert.Equal("1003", api.LastReregisterRequest?.TargetStoreCode);
        Assert.NotNull(reregistered);
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-client-device-{Guid.NewGuid():N}.db");
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }

    private sealed class FakeDeviceApiClient : IDeviceApiClient
    {
        public IReadOnlyList<StoreSelectionItem> Stores { get; init; } = [];

        public DeviceRegisterResponse? RegisterResponse { get; init; }

        public DeviceVerifyResponse? VerifyResponse { get; init; }

        public DeviceReregisterResponse? ReregisterResponse { get; init; }

        public DeviceReregisterRequest? LastReregisterRequest { get; private set; }

        public Task<IReadOnlyList<StoreSelectionItem>> GetStoresAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<StoreSelectionItem>>(
                Stores
                    .OrderBy(x => x.StoreName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(x => x.StoreCode, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        }

        public Task<DeviceRegisterResponse> RegisterAsync(
            DeviceRegisterRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(RegisterResponse!);
        }

        public Task<DeviceVerifyResponse> VerifyAsync(
            DeviceVerifyRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(VerifyResponse!);
        }

        public Task<DeviceReregisterResponse> ReregisterAsync(
            DeviceReregisterRequest request,
            CancellationToken cancellationToken = default)
        {
            LastReregisterRequest = request;
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
    }

    private sealed class FakeFingerprintService(string hardwareId) : IDeviceFingerprintService
    {
        public string GetHardwareId() => hardwareId;
    }

    private sealed class FakeAuthorizationProtector : IDeviceAuthorizationProtector
    {
        public string? LastProtectedValue { get; private set; }

        public string? Protect(string? value)
        {
            LastProtectedValue = value;
            return string.IsNullOrWhiteSpace(value) ? null : $"protected:{value}";
        }

        public string? Unprotect(string? protectedValue)
        {
            return protectedValue?.StartsWith("protected:", StringComparison.Ordinal) == true
                ? protectedValue["protected:".Length..]
                : null;
        }
    }
}
