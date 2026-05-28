using Hbpos.Api.Services;
using Hbpos.Contracts.Devices;

namespace Hbpos.Api.Tests;

public sealed class DeviceServiceTests
{
    [Fact]
    public async Task RegisterAsync_WhenPendingDeviceExistsForDifferentStore_DisablesOldPendingAndCreatesNewPending()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            LatestByHardwareId = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1002_1011",
                StoreCode = "1002",
                HardwareId = "HW-001",
                DeviceStatus = -1,
                AuthorizationCode = "AUTH-OLD"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal("1003", response.StoreCode);
        Assert.Equal("Chermside", response.StoreName);
        Assert.Equal(-1, response.DeviceStatus);
        Assert.False(response.IsAllowed);
        Assert.Equal("Device registration is pending approval.", response.Message);

        Assert.Equal(1, repository.TransactionCallCount);
        Assert.Single(repository.DisabledRequests);
        Assert.Single(repository.CreatedRegistrations);

        var disabled = repository.DisabledRequests[0];
        Assert.Equal("HW-001", disabled.HardwareId);
        Assert.Equal("1002", disabled.StoreCode);
        Assert.Equal("POS_1002_1011", disabled.DeviceCode);

        var created = repository.CreatedRegistrations[0];
        Assert.Equal("HW-001", created.HardwareId);
        Assert.Equal("1003", created.StoreCode);
        Assert.Equal(-1, created.DeviceStatus);
        Assert.Equal("HBPOS_CLIENT", created.CreatedBy);
        Assert.Contains("Counter 2", created.Remark, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RegisterAsync_WhenDifferentStoreExistingDeviceIsNotPending_RejectsWithoutWrites(int existingStatus)
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            LatestByHardwareId = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1002_1011",
                StoreCode = "1002",
                HardwareId = "HW-001",
                DeviceStatus = existingStatus,
                AuthorizationCode = existingStatus == 1 ? "AUTH-001" : null
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal("POS_1002_1011", response.DeviceCode);
        Assert.Equal("1002", response.StoreCode);
        Assert.Equal(string.Empty, response.StoreName);
        Assert.Equal(existingStatus, response.DeviceStatus);
        Assert.False(response.IsAllowed);
        Assert.Equal("Device hardware is already registered to another store.", response.Message);
        Assert.Empty(repository.DisabledRequests);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Equal(0, repository.TransactionCallCount);
    }

    [Fact]
    public async Task RegisterAsync_WhenHardwareHasActiveRegistration_RejectsPendingStoreSwitch()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            LatestByHardwareId = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1002_1011",
                StoreCode = "1002",
                HardwareId = "HW-001",
                DeviceStatus = -1
            },
            ActiveOrLockedRegistration = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_ACTIVE",
                StoreCode = "1002",
                HardwareId = "HW-001",
                DeviceStatus = 1,
                AuthorizationCode = "AUTH-001"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal("POS_ACTIVE", response.DeviceCode);
        Assert.Equal("1002", response.StoreCode);
        Assert.Equal(string.Empty, response.StoreName);
        Assert.Equal(1, response.DeviceStatus);
        Assert.Equal("Device hardware is already registered to another store.", response.Message);
        Assert.Empty(repository.DisabledRequests);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Equal(0, repository.TransactionCallCount);
    }

    [Fact]
    public async Task RegisterAsync_WhenPendingSwitchUpdateMisses_DoesNotCreateNewRegistration()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            LatestByHardwareId = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1002_1011",
                StoreCode = "1002",
                HardwareId = "HW-001",
                DeviceStatus = -1
            },
            DisablePendingRowsAffected = 0
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal("POS_1002_1011", response.DeviceCode);
        Assert.Equal("1002", response.StoreCode);
        Assert.Equal(string.Empty, response.StoreName);
        Assert.Equal(0, response.DeviceStatus);
        Assert.Equal("Pending device registration changed. Please reload stores and try again.", response.Message);
        Assert.Single(repository.DisabledRequests);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Equal(1, repository.TransactionCallCount);
    }

    [Fact]
    public async Task RegisterAsync_WhenSameHardwareAndStore_ReturnsExistingRegistration()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            LatestByHardwareId = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1002_1011",
                StoreCode = "1002",
                HardwareId = "HW-001",
                DeviceStatus = 1,
                AuthorizationCode = "AUTH-001"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1002", "HW-001", "Counter 1"),
            CancellationToken.None);

        Assert.Equal("POS_1002_1011", response.DeviceCode);
        Assert.Equal("1002", response.StoreCode);
        Assert.Equal("Lutwyche", response.StoreName);
        Assert.Equal(1, response.DeviceStatus);
        Assert.True(response.IsAllowed);
        Assert.Equal("AUTH-001", response.AuthorizationCode);
        Assert.Empty(repository.DisabledRequests);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Equal(0, repository.TransactionCallCount);
    }

    private static Task<DeviceStoreInfo?> LoadStoreAsync(string storeCode, CancellationToken cancellationToken)
    {
        DeviceStoreInfo? store = storeCode switch
        {
            "1002" => new DeviceStoreInfo("1002", "Lutwyche"),
            "1003" => new DeviceStoreInfo("1003", "Chermside"),
            _ => null
        };

        return Task.FromResult(store);
    }

    private sealed class FakeDeviceRegistrationRepository : IDeviceRegistrationRepository
    {
        public DeviceRegistrationRecord? LatestByHardwareId { get; init; }

        public DeviceRegistrationRecord? ActiveOrLockedRegistration { get; init; }

        public int DisablePendingRowsAffected { get; init; } = 1;

        public List<DeviceRegistrationDisableRequest> DisabledRequests { get; } = [];

        public List<DeviceRegistrationCreateRequest> CreatedRegistrations { get; } = [];

        public int TransactionCallCount { get; private set; }

        public Task<DeviceRegistrationRecord?> FindByDeviceCodeAsync(
            string deviceCode,
            string storeCode,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<DeviceRegistrationRecord?>(null);
        }

        public Task<DeviceRegistrationRecord?> FindLatestByHardwareIdAsync(
            string hardwareId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(LatestByHardwareId);
        }

        public Task<DeviceRegistrationRecord?> FindActiveOrLockedRegistrationAsync(
            string hardwareId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(ActiveOrLockedRegistration);
        }

        public Task<int> DisablePendingRegistrationAsync(
            DeviceRegistrationDisableRequest request,
            CancellationToken cancellationToken)
        {
            DisabledRequests.Add(request);
            return Task.FromResult(DisablePendingRowsAffected);
        }

        public Task CreateRegistrationAsync(
            DeviceRegistrationCreateRequest request,
            CancellationToken cancellationToken)
        {
            CreatedRegistrations.Add(request);
            return Task.CompletedTask;
        }

        public async Task ExecuteInTransactionAsync(
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken)
        {
            TransactionCallCount++;
            await action(cancellationToken);
        }
    }
}
