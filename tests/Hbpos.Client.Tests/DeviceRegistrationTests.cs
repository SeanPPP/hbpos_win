using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

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
                new Hbpos.Contracts.Devices.DeviceRegisterResponse("POS-001", "1002", "Lutwyche", 1, true, "Device is enabled.", "AUTH-001"),
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
    public async Task DeviceRegistrationViewModel_LoadsStoresAndMapsPendingRegistrationResult()
    {
        var workflow = new FakeDeviceRegistrationWorkflowService
        {
            HardwareId = "HW-001",
            LoadResult = new DeviceRegistrationLoadResult(
                [new StoreSelectionItem("1002", "Lutwyche", true), new StoreSelectionItem("1003", "Zillmere", true)],
                new StoreSelectionItem("1002", "Lutwyche", true),
                string.Empty,
                false,
                "Select a store and submit this register for approval."),
            RegisterResult = new DeviceRegistrationActionResult(
                "POS-001",
                "1002",
                "Lutwyche",
                "HW-001",
                true,
                "Pending approval",
                null,
                false,
                false)
        };
        var viewModel = new DeviceRegistrationViewModel(workflow);

        await viewModel.InitializeAsync(cachedDevice: null);
        await viewModel.RegisterCommand.ExecuteAsync(null);

        Assert.Equal("HW-001", viewModel.HardwareId);
        Assert.Equal("Lutwyche (1002)", viewModel.SelectedStore?.DisplayName);
        Assert.Equal("POS-001", viewModel.DeviceCode);
        Assert.True(viewModel.HasPendingRegistration);
        Assert.Equal("Pending approval", viewModel.StatusMessage);
        Assert.Equal("1002", workflow.LastRegisterStoreCode);
        Assert.Equal("HW-001", workflow.LastRegisterHardwareId);
    }

    [Fact]
    public async Task DeviceRegistrationViewModel_VerifyRaisesActivationWhenWorkflowReturnsActivated()
    {
        var workflow = new FakeDeviceRegistrationWorkflowService
        {
            HardwareId = "HW-001",
            LoadResult = new DeviceRegistrationLoadResult(
                [new StoreSelectionItem("1002", "Lutwyche", true)],
                new StoreSelectionItem("1002", "Lutwyche", true),
                "POS-001",
                true,
                "Pending approval"),
            VerifyResult = new DeviceRegistrationActionResult(
                "POS-001",
                "1002",
                "Lutwyche",
                "HW-001",
                false,
                "Device is enabled.",
                "AUTH-001",
                true,
                false)
        };
        var viewModel = new DeviceRegistrationViewModel(workflow);
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
        Assert.Equal("POS-001", workflow.LastVerifyDeviceCode);
        Assert.Equal("1002", workflow.LastVerifyStoreCode);
    }

    [Fact]
    public async Task DeviceRegistrationViewModel_PendingRegistration_AllowsSwitchingStoreBeforeSubmit()
    {
        var workflow = new FakeDeviceRegistrationWorkflowService
        {
            HardwareId = "HW-001",
            LoadResult = new DeviceRegistrationLoadResult(
                [new StoreSelectionItem("1002", "Lutwyche", true), new StoreSelectionItem("1003", "Zillmere", true)],
                new StoreSelectionItem("1002", "Lutwyche", true),
                "POS-OLD",
                true,
                "Pending approval"),
            RegisterResult = new DeviceRegistrationActionResult(
                "POS-NEW",
                "1003",
                "Zillmere",
                "HW-001",
                true,
                "Pending approval",
                null,
                false,
                false),
            VerifyResult = new DeviceRegistrationActionResult(
                "POS-NEW",
                "1003",
                "Zillmere",
                "HW-001",
                true,
                "Pending approval",
                null,
                false,
                false)
        };
        var viewModel = new DeviceRegistrationViewModel(workflow);
        var cached = new LocalDeviceCache("POS-OLD", "1002", "Lutwyche", "HW-001", -1, false, "Pending approval", DateTimeOffset.UtcNow);

        await viewModel.InitializeAsync(cached);

        Assert.Equal("POS-OLD", viewModel.DeviceCode);
        Assert.True(viewModel.HasPendingRegistration);
        Assert.False(viewModel.RegisterCommand.CanExecute(null));
        Assert.True(viewModel.VerifyCommand.CanExecute(null));

        viewModel.SelectedStore = viewModel.Stores.Single(store => store.StoreCode == "1003");

        Assert.Empty(viewModel.DeviceCode);
        Assert.False(viewModel.HasPendingRegistration);
        Assert.True(viewModel.RegisterCommand.CanExecute(null));
        Assert.False(viewModel.VerifyCommand.CanExecute(null));
        Assert.Equal("Submit Store Switch Registration", viewModel.RegisterButtonText);

        viewModel.SelectedStore = viewModel.Stores.Single(store => store.StoreCode == "1002");

        Assert.Equal("POS-OLD", viewModel.DeviceCode);
        Assert.True(viewModel.HasPendingRegistration);
        Assert.False(viewModel.RegisterCommand.CanExecute(null));
        Assert.True(viewModel.VerifyCommand.CanExecute(null));

        viewModel.SelectedStore = viewModel.Stores.Single(store => store.StoreCode == "1003");
        await viewModel.RegisterCommand.ExecuteAsync(null);

        Assert.Equal("1003", workflow.LastRegisterStoreCode);
        Assert.Equal("POS-NEW", viewModel.DeviceCode);
        Assert.True(viewModel.HasPendingRegistration);
        Assert.False(viewModel.RegisterCommand.CanExecute(null));
        Assert.True(viewModel.VerifyCommand.CanExecute(null));

        await viewModel.VerifyCommand.ExecuteAsync(null);

        Assert.Equal("1003", workflow.LastVerifyStoreCode);
        Assert.Equal("POS-NEW", workflow.LastVerifyDeviceCode);
    }

    [Fact]
    public async Task DeviceRegistrationViewModel_PendingRegistration_AllowsSwitchWhenCachedStoreIsNotVisible()
    {
        var workflow = new FakeDeviceRegistrationWorkflowService
        {
            HardwareId = "HW-001",
            LoadResult = new DeviceRegistrationLoadResult(
                [new StoreSelectionItem("1003", "Zillmere", true)],
                new StoreSelectionItem("1003", "Zillmere", true),
                "POS-OLD",
                true,
                "Pending approval")
        };
        var viewModel = new DeviceRegistrationViewModel(workflow);
        var cached = new LocalDeviceCache("POS-OLD", "1002", "Lutwyche", "HW-001", -1, false, "Pending approval", DateTimeOffset.UtcNow);

        await viewModel.InitializeAsync(cached);

        Assert.Equal("1003", viewModel.SelectedStore?.StoreCode);
        Assert.Empty(viewModel.DeviceCode);
        Assert.False(viewModel.HasPendingRegistration);
        Assert.True(viewModel.RegisterCommand.CanExecute(null));
        Assert.False(viewModel.VerifyCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeviceRegistrationViewModel_RejectedCachedDevice_DoesNotBecomePendingRegistration()
    {
        var workflow = new FakeDeviceRegistrationWorkflowService
        {
            HardwareId = "HW-001",
            LoadResult = new DeviceRegistrationLoadResult(
                [new StoreSelectionItem("1003", "Zillmere", true)],
                new StoreSelectionItem("1003", "Zillmere", true),
                "POS-OLD",
                false,
                "Device hardware is already registered to another store.")
        };
        var viewModel = new DeviceRegistrationViewModel(workflow);
        var cached = new LocalDeviceCache("POS-OLD", "1002", "Lutwyche", "HW-001", 1, false, "Device hardware is already registered to another store.", DateTimeOffset.UtcNow);

        await viewModel.InitializeAsync(cached);

        Assert.False(viewModel.HasPendingRegistration);
        Assert.Equal("POS-OLD", viewModel.DeviceCode);
        Assert.True(viewModel.RegisterCommand.CanExecute(null));
        Assert.True(viewModel.VerifyCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeviceRegistrationViewModel_RejectedStoreSwitch_DoesNotBecomePendingRegistration()
    {
        var workflow = new FakeDeviceRegistrationWorkflowService
        {
            HardwareId = "HW-001",
            LoadResult = new DeviceRegistrationLoadResult(
                [new StoreSelectionItem("1002", "Lutwyche", true), new StoreSelectionItem("1003", "Zillmere", true)],
                new StoreSelectionItem("1002", "Lutwyche", true),
                "POS-OLD",
                true,
                "Pending approval"),
            RegisterResult = new DeviceRegistrationActionResult(
                "POS-OLD",
                "1002",
                "Lutwyche",
                "HW-001",
                false,
                "Device hardware is already registered to another store.",
                null,
                false,
                false)
        };
        var viewModel = new DeviceRegistrationViewModel(workflow);
        var cached = new LocalDeviceCache("POS-OLD", "1002", "Lutwyche", "HW-001", -1, false, "Pending approval", DateTimeOffset.UtcNow);

        await viewModel.InitializeAsync(cached);
        viewModel.SelectedStore = viewModel.Stores.Single(store => store.StoreCode == "1003");
        await viewModel.RegisterCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.DeviceCode);
        Assert.False(viewModel.HasPendingRegistration);
        Assert.True(viewModel.RegisterCommand.CanExecute(null));
        Assert.False(viewModel.VerifyCommand.CanExecute(null));
        Assert.Equal("Device hardware is already registered to another store.", viewModel.StatusMessage);

        viewModel.SelectedStore = viewModel.Stores.Single(store => store.StoreCode == "1002");

        Assert.Empty(viewModel.DeviceCode);
        Assert.False(viewModel.HasPendingRegistration);
        Assert.True(viewModel.RegisterCommand.CanExecute(null));
        Assert.False(viewModel.VerifyCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeviceRegistrationViewModel_ReregisterMode_MapsResultAndRaisesReregistered()
    {
        var workflow = new FakeDeviceRegistrationWorkflowService
        {
            HardwareId = "HW-001",
            LoadResult = new DeviceRegistrationLoadResult(
                [new StoreSelectionItem("1003", "Zillmere", true)],
                new StoreSelectionItem("1003", "Zillmere", true),
                string.Empty,
                false,
                "Select a new store and submit device reregistration."),
            ReregisterResult = new DeviceRegistrationActionResult(
                "POS-NEW",
                "1003",
                "Zillmere",
                "HW-001",
                true,
                "Pending approval",
                null,
                false,
                true)
        };
        var viewModel = new DeviceRegistrationViewModel(workflow);
        DeviceReregisteredEventArgs? reregistered = null;
        viewModel.DeviceReregistered += (_, args) => reregistered = args;

        viewModel.PrepareReregister("1002");
        await viewModel.LoadStoresAsync(cachedDevice: null);

        Assert.Equal("Reregister Device to Another Store", viewModel.TitleText);
        Assert.Equal("Submit Store Switch Reregistration", viewModel.RegisterButtonText);

        Assert.Null(viewModel.SelectedStore);
        Assert.False(viewModel.RegisterCommand.CanExecute(null));

        viewModel.SelectedStore = Assert.Single(viewModel.Stores);
        await viewModel.RegisterCommand.ExecuteAsync(null);

        Assert.Equal("1002", workflow.LastLoadExcludedStoreCode);
        Assert.Equal("Zillmere (1003)", viewModel.SelectedStore?.DisplayName);
        Assert.Equal("POS-NEW", viewModel.DeviceCode);
        Assert.True(viewModel.HasPendingRegistration);
        Assert.Equal("Pending approval", viewModel.StatusMessage);
        Assert.False(viewModel.IsReregisterMode);
        Assert.False(viewModel.CanCancel);
        Assert.Equal("1003", workflow.LastReregisterStoreCode);
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

    private sealed class FakeDeviceRegistrationWorkflowService : IDeviceRegistrationWorkflowService
    {
        public string HardwareId { get; init; } = "HW-001";

        public DeviceRegistrationLoadResult LoadResult { get; init; } = new([], null, string.Empty, false, string.Empty);

        public DeviceRegistrationActionResult RegisterResult { get; init; } = new(string.Empty, string.Empty, string.Empty, string.Empty, false, string.Empty, null, false, false);

        public DeviceRegistrationActionResult VerifyResult { get; init; } = new(string.Empty, string.Empty, string.Empty, string.Empty, false, string.Empty, null, false, false);

        public DeviceRegistrationActionResult ReregisterResult { get; init; } = new(string.Empty, string.Empty, string.Empty, string.Empty, false, string.Empty, null, false, false);

        public string? LastLoadExcludedStoreCode { get; private set; }

        public string? LastRegisterStoreCode { get; private set; }

        public string? LastRegisterHardwareId { get; private set; }

        public string? LastVerifyStoreCode { get; private set; }

        public string? LastVerifyDeviceCode { get; private set; }

        public string? LastReregisterStoreCode { get; private set; }

        public string GetHardwareId() => HardwareId;

        public Task<DeviceRegistrationLoadResult> LoadStoresAsync(
            LocalDeviceCache? cachedDevice,
            bool isReregisterMode,
            string? excludedStoreCode = null,
            CancellationToken cancellationToken = default)
        {
            LastLoadExcludedStoreCode = excludedStoreCode;
            return Task.FromResult(LoadResult);
        }

        public Task<DeviceRegistrationActionResult> RegisterAsync(
            StoreSelectionItem selectedStore,
            string hardwareId,
            CancellationToken cancellationToken = default)
        {
            LastRegisterStoreCode = selectedStore.StoreCode;
            LastRegisterHardwareId = hardwareId;
            return Task.FromResult(RegisterResult);
        }

        public Task<DeviceRegistrationActionResult> VerifyAsync(
            StoreSelectionItem selectedStore,
            string deviceCode,
            string hardwareId,
            CancellationToken cancellationToken = default)
        {
            LastVerifyStoreCode = selectedStore.StoreCode;
            LastVerifyDeviceCode = deviceCode;
            return Task.FromResult(VerifyResult);
        }

        public Task<DeviceRegistrationActionResult> ReregisterAsync(
            StoreSelectionItem selectedStore,
            string hardwareId,
            CancellationToken cancellationToken = default)
        {
            LastReregisterStoreCode = selectedStore.StoreCode;
            return Task.FromResult(ReregisterResult);
        }
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

