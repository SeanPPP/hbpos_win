using Hbpos.Api.Data;
using Hbpos.Contracts.Devices;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface IDeviceService
{
    Task<DeviceRegisterResponse> RegisterAsync(DeviceRegisterRequest request, CancellationToken cancellationToken);

    Task<DeviceVerifyResponse> VerifyAsync(DeviceVerifyRequest request, CancellationToken cancellationToken);

    Task<DeviceReregisterResponse> ReregisterAsync(
        DeviceReregisterRequest request,
        DeviceReregisterContext currentDevice,
        CancellationToken cancellationToken);
}

public interface IDeviceRegistrationRepository
{
    Task<DeviceRegistrationRecord?> FindLatestByHardwareIdAsync(
        string hardwareId,
        CancellationToken cancellationToken);

    Task<DeviceRegistrationRecord?> FindByDeviceCodeAsync(
        string deviceCode,
        string storeCode,
        CancellationToken cancellationToken);

    Task<DeviceRegistrationRecord?> FindActiveOrLockedRegistrationAsync(
        string hardwareId,
        CancellationToken cancellationToken);

    Task<int> DisablePendingRegistrationAsync(
        DeviceRegistrationDisableRequest request,
        CancellationToken cancellationToken);

    Task CreateRegistrationAsync(
        DeviceRegistrationCreateRequest request,
        CancellationToken cancellationToken);

    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken);
}

public sealed record DeviceReregisterContext(
    string DeviceCode,
    string StoreCode,
    string HardwareId);

public sealed record DeviceStoreInfo(
    string StoreCode,
    string StoreName);

public sealed class DeviceRegistrationRecord
{
    public string? DeviceCode { get; set; }

    public string? StoreCode { get; set; }

    public string? HardwareId { get; set; }

    public int DeviceStatus { get; set; }

    public string? AuthorizationCode { get; set; }
}

public sealed class DeviceRegistrationDisableRequest
{
    public string HardwareId { get; init; } = string.Empty;

    public string StoreCode { get; init; } = string.Empty;

    public string DeviceCode { get; init; } = string.Empty;

    public string RemarkSuffix { get; init; } = string.Empty;
}

public sealed class DeviceRegistrationCreateRequest
{
    public string HardwareId { get; init; } = string.Empty;

    public string DeviceCode { get; init; } = string.Empty;

    public string StoreCode { get; init; } = string.Empty;

    public int DeviceStatus { get; init; }

    public string AuthorizationCode { get; init; } = string.Empty;

    public string Remark { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; }

    public string CreatedBy { get; init; } = string.Empty;

    public string DeviceType { get; init; } = "POS";

    public string DeviceSystem { get; init; } = "Windows";
}

public sealed class DeviceService : IDeviceService
{
    private const int PendingStatus = -1;
    private const int DisabledStatus = 0;
    private const int EnabledStatus = 1;
    private const int LockedStatus = 2;
    private const int UnregisteredStatus = 3;

    private readonly HbposSqlSugarContext? dbContext;
    private readonly IDeviceRegistrationRepository deviceRegistrationRepository;
    private readonly Func<string, CancellationToken, Task<DeviceStoreInfo?>> loadStoreAsync;

    public DeviceService(
        HbposSqlSugarContext dbContext,
        IDeviceRegistrationRepository deviceRegistrationRepository)
    {
        this.dbContext = dbContext;
        this.deviceRegistrationRepository = deviceRegistrationRepository;
        loadStoreAsync = LoadStoreAsync;
    }

    public DeviceService(
        IDeviceRegistrationRepository deviceRegistrationRepository,
        Func<string, CancellationToken, Task<DeviceStoreInfo?>> loadStoreAsync)
    {
        this.deviceRegistrationRepository = deviceRegistrationRepository;
        this.loadStoreAsync = loadStoreAsync;
    }

    public async Task<DeviceRegisterResponse> RegisterAsync(
        DeviceRegisterRequest request,
        CancellationToken cancellationToken)
    {
        var storeCode = Normalize(request.StoreCode);
        var hardwareId = Normalize(request.HardwareId);
        var terminalName = Normalize(request.TerminalName);

        if (string.IsNullOrEmpty(storeCode))
        {
            return CreateRegisterResponse(string.Empty, storeCode, string.Empty, UnregisteredStatus, "storeCode is required");
        }

        if (string.IsNullOrEmpty(hardwareId))
        {
            return CreateRegisterResponse(string.Empty, storeCode, string.Empty, UnregisteredStatus, "hardwareId is required");
        }

        var store = await loadStoreAsync(storeCode, cancellationToken);
        if (store is null)
        {
            return CreateRegisterResponse(string.Empty, storeCode, string.Empty, UnregisteredStatus, "Store was not found or inactive.");
        }

        var existing = await deviceRegistrationRepository.FindLatestByHardwareIdAsync(hardwareId, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
            {
                if (existing.DeviceStatus != PendingStatus)
                {
                    return CreateRegisterResponse(
                        existing.DeviceCode ?? string.Empty,
                        existing.StoreCode ?? storeCode,
                        string.Empty,
                        existing.DeviceStatus,
                        "Device hardware is already registered to another store.");
                }

                var activeOrLockedRegistration = await deviceRegistrationRepository
                    .FindActiveOrLockedRegistrationAsync(hardwareId, cancellationToken);
                if (activeOrLockedRegistration is not null)
                {
                    return CreateRegisterResponse(
                        activeOrLockedRegistration.DeviceCode ?? existing.DeviceCode ?? string.Empty,
                        activeOrLockedRegistration.StoreCode ?? existing.StoreCode ?? storeCode,
                        string.Empty,
                        activeOrLockedRegistration.DeviceStatus,
                        "Device hardware is already registered to another store.");
                }

                var now = DateTime.Now;
                var pendingRegistration = CreatePendingRegistration(
                    hardwareId,
                    storeCode,
                    terminalName,
                    now);

                var disableRequest = new DeviceRegistrationDisableRequest
                {
                    HardwareId = hardwareId,
                    StoreCode = existing.StoreCode ?? string.Empty,
                    DeviceCode = existing.DeviceCode ?? string.Empty,
                    RemarkSuffix = $" | Disabled by registration switch to {storeCode} at {now:O}"
                };

                var switchSubmitted = false;
                await deviceRegistrationRepository.ExecuteInTransactionAsync(
                    async token =>
                    {
                        var disabledCount = await deviceRegistrationRepository.DisablePendingRegistrationAsync(disableRequest, token);
                        if (disabledCount != 1)
                        {
                            return;
                        }

                        await deviceRegistrationRepository.CreateRegistrationAsync(pendingRegistration, token);
                        switchSubmitted = true;
                    },
                    cancellationToken);

                if (!switchSubmitted)
                {
                    return CreateRegisterResponse(
                        existing.DeviceCode ?? string.Empty,
                        existing.StoreCode ?? storeCode,
                        string.Empty,
                        DisabledStatus,
                        "Pending device registration changed. Please reload stores and try again.");
                }

                return new DeviceRegisterResponse(
                    pendingRegistration.DeviceCode,
                    storeCode,
                    store.StoreName,
                    PendingStatus,
                    false,
                    GetStatusMessage(PendingStatus),
                    null);
            }

            return new DeviceRegisterResponse(
                existing.DeviceCode ?? string.Empty,
                storeCode,
                store.StoreName,
                existing.DeviceStatus,
                existing.DeviceStatus == EnabledStatus,
                GetStatusMessage(existing.DeviceStatus),
                existing.DeviceStatus == EnabledStatus ? existing.AuthorizationCode : null);
        }

        var newRegistration = CreatePendingRegistration(
            hardwareId,
            storeCode,
            terminalName,
            DateTime.Now);

        await deviceRegistrationRepository.CreateRegistrationAsync(newRegistration, cancellationToken);

        return new DeviceRegisterResponse(
            newRegistration.DeviceCode,
            storeCode,
            store.StoreName,
            PendingStatus,
            false,
            GetStatusMessage(PendingStatus),
            null);
    }

    public async Task<DeviceVerifyResponse> VerifyAsync(
        DeviceVerifyRequest request,
        CancellationToken cancellationToken)
    {
        var deviceCode = Normalize(request.DeviceCode);
        var storeCode = Normalize(request.StoreCode);
        var hardwareId = Normalize(request.HardwareId);

        var store = await loadStoreAsync(storeCode, cancellationToken);
        if (store is null)
        {
            return CreateVerifyResponse(deviceCode, storeCode, string.Empty, UnregisteredStatus, "Store was not found or inactive.");
        }

        var device = await deviceRegistrationRepository.FindByDeviceCodeAsync(deviceCode, storeCode, cancellationToken);
        if (device is null)
        {
            return CreateVerifyResponse(deviceCode, storeCode, store.StoreName, UnregisteredStatus, "Device is not registered.");
        }

        if (!string.IsNullOrWhiteSpace(hardwareId)
            && !string.Equals(device.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase))
        {
            return CreateVerifyResponse(deviceCode, storeCode, store.StoreName, device.DeviceStatus, "Device hardware id does not match.");
        }

        return new DeviceVerifyResponse(
            deviceCode,
            storeCode,
            store.StoreName,
            device.DeviceStatus,
            device.DeviceStatus == EnabledStatus,
            GetStatusMessage(device.DeviceStatus),
            device.DeviceStatus == EnabledStatus ? device.AuthorizationCode : null);
    }

    public async Task<DeviceReregisterResponse> ReregisterAsync(
        DeviceReregisterRequest request,
        DeviceReregisterContext currentDevice,
        CancellationToken cancellationToken)
    {
        var targetStoreCode = Normalize(request.TargetStoreCode);
        var hardwareId = Normalize(request.HardwareId);
        var currentDeviceCode = Normalize(currentDevice.DeviceCode);
        var currentStoreCode = Normalize(currentDevice.StoreCode);
        var currentHardwareId = Normalize(currentDevice.HardwareId);
        var terminalName = Normalize(request.TerminalName);

        if (string.IsNullOrEmpty(targetStoreCode))
        {
            return CreateReregisterResponse(string.Empty, targetStoreCode, string.Empty, UnregisteredStatus, "targetStoreCode is required");
        }

        if (string.IsNullOrEmpty(hardwareId))
        {
            return CreateReregisterResponse(string.Empty, targetStoreCode, string.Empty, UnregisteredStatus, "hardwareId is required");
        }

        if (!string.Equals(hardwareId, currentHardwareId, StringComparison.OrdinalIgnoreCase))
        {
            return CreateReregisterResponse(currentDeviceCode, currentStoreCode, string.Empty, DisabledStatus, "Device hardware id does not match.");
        }

        if (string.Equals(targetStoreCode, currentStoreCode, StringComparison.OrdinalIgnoreCase))
        {
            return CreateReregisterResponse(currentDeviceCode, currentStoreCode, string.Empty, DisabledStatus, "Please select a different store for device reregistration.");
        }

        var store = await loadStoreAsync(targetStoreCode, cancellationToken);
        if (store is null)
        {
            return CreateReregisterResponse(string.Empty, targetStoreCode, string.Empty, UnregisteredStatus, "Store was not found or inactive.");
        }

        var deviceCode = CreateDeviceCode(targetStoreCode, DateTime.Now);
        var authorizationCode = Guid.NewGuid().ToString("N");
        var now = DateTime.Now;
        var remark = string.IsNullOrWhiteSpace(terminalName)
            ? $"HBPOS client reregistration from {currentStoreCode}/{currentDeviceCode}"
            : $"HBPOS client reregistration from {currentStoreCode}/{currentDeviceCode}: {terminalName}";

        var context = dbContext ?? throw new InvalidOperationException("Db context is required for device reregistration.");

        await context.PosmDb.Ado.BeginTranAsync();
        try
        {
            const string disableSql = """
                UPDATE [POSM_设备注册信息表]
                SET [设备状态] = @DisabledStatus,
                    [备注] = CONCAT(ISNULL([备注], ''), @RemarkSuffix)
                WHERE [系统设备编号] = @CurrentDeviceCode
                  AND [分店代码] = @CurrentStoreCode
                  AND [设备硬件识别码] = @HardwareId
                  AND [设备状态] = @EnabledStatus;
                """;

            await context.PosmDb.Ado.ExecuteCommandAsync(
                disableSql,
                new SugarParameter("@DisabledStatus", DisabledStatus),
                new SugarParameter("@RemarkSuffix", $" | Disabled by reregistration to {targetStoreCode} at {now:O}"),
                new SugarParameter("@CurrentDeviceCode", currentDeviceCode),
                new SugarParameter("@CurrentStoreCode", currentStoreCode),
                new SugarParameter("@HardwareId", hardwareId),
                new SugarParameter("@EnabledStatus", EnabledStatus));

            const string insertSql = """
                INSERT INTO [POSM_设备注册信息表]
                    ([设备硬件识别码], [系统设备编号], [分店代码], [设备类型], [设备系统], [设备状态], [设备授权码], [备注], [创建时间], [创建人])
                VALUES
                    (@HardwareId, @DeviceCode, @StoreCode, @DeviceType, @DeviceSystem, @DeviceStatus, @AuthorizationCode, @Remark, @CreatedAt, @CreatedBy);
                """;

            await context.PosmDb.Ado.ExecuteCommandAsync(
                insertSql,
                new SugarParameter("@HardwareId", hardwareId),
                new SugarParameter("@DeviceCode", deviceCode),
                new SugarParameter("@StoreCode", targetStoreCode),
                new SugarParameter("@DeviceType", "POS"),
                new SugarParameter("@DeviceSystem", "Windows"),
                new SugarParameter("@DeviceStatus", PendingStatus),
                new SugarParameter("@AuthorizationCode", authorizationCode),
                new SugarParameter("@Remark", remark),
                new SugarParameter("@CreatedAt", now),
                new SugarParameter("@CreatedBy", "HBPOS_CLIENT"));

            await context.PosmDb.Ado.CommitTranAsync();
        }
        catch
        {
            await context.PosmDb.Ado.RollbackTranAsync();
            throw;
        }

        return new DeviceReregisterResponse(
            deviceCode,
            targetStoreCode,
            store.StoreName,
            PendingStatus,
            false,
            GetStatusMessage(PendingStatus),
            null);
    }

    internal static string CreateDeviceCode(string storeCode, DateTime localTime)
    {
        return $"POS_{storeCode}_{localTime:HHmm}";
    }

    private async Task<DeviceStoreInfo?> LoadStoreAsync(string storeCode, CancellationToken cancellationToken)
    {
        var context = dbContext ?? throw new InvalidOperationException("Db context is required for store lookup.");

        var store = await context.MainDb.Queryable<BlazorApp.Shared.Models.Store>()
            .FirstAsync(x => x.StoreCode == storeCode && x.IsActive && !x.IsDeleted, cancellationToken);

        return store is null
            ? null
            : new DeviceStoreInfo(store.StoreCode, store.StoreName);
    }

    private static DeviceRegistrationCreateRequest CreatePendingRegistration(
        string hardwareId,
        string storeCode,
        string terminalName,
        DateTime createdAt)
    {
        return new DeviceRegistrationCreateRequest
        {
            HardwareId = hardwareId,
            DeviceCode = CreateDeviceCode(storeCode, createdAt),
            StoreCode = storeCode,
            DeviceStatus = PendingStatus,
            AuthorizationCode = Guid.NewGuid().ToString("N"),
            Remark = string.IsNullOrWhiteSpace(terminalName)
                ? "HBPOS client registration"
                : $"HBPOS client registration: {terminalName}",
            CreatedAt = createdAt,
            CreatedBy = "HBPOS_CLIENT"
        };
    }

    private static DeviceRegisterResponse CreateRegisterResponse(
        string deviceCode,
        string storeCode,
        string storeName,
        int status,
        string message)
    {
        return new DeviceRegisterResponse(deviceCode, storeCode, storeName, status, false, message);
    }

    private static DeviceVerifyResponse CreateVerifyResponse(
        string deviceCode,
        string storeCode,
        string storeName,
        int status,
        string message)
    {
        return new DeviceVerifyResponse(deviceCode, storeCode, storeName, status, false, message);
    }

    private static DeviceReregisterResponse CreateReregisterResponse(
        string deviceCode,
        string storeCode,
        string storeName,
        int status,
        string message)
    {
        return new DeviceReregisterResponse(deviceCode, storeCode, storeName, status, false, message);
    }

    private static string GetStatusMessage(int status)
    {
        return status switch
        {
            PendingStatus => "Device registration is pending approval.",
            DisabledStatus => "Device is disabled.",
            EnabledStatus => "Device is enabled.",
            LockedStatus => "Device is locked.",
            UnregisteredStatus => "Device is not registered.",
            _ => "Device status is unknown."
        };
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim();
    }
}

public sealed class SqlSugarDeviceRegistrationRepository(HbposSqlSugarContext dbContext) : IDeviceRegistrationRepository
{
    public async Task<DeviceRegistrationRecord?> FindLatestByHardwareIdAsync(
        string hardwareId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [系统设备编号] AS DeviceCode,
                [分店代码] AS StoreCode,
                [设备硬件识别码] AS HardwareId,
                [设备状态] AS DeviceStatus,
                [设备授权码] AS AuthorizationCode
            FROM [POSM_设备注册信息表]
            WHERE [设备硬件识别码] = @HardwareId
            ORDER BY [ID] DESC;
            """;

        var record = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<DeviceRegistrationRecord>(
            sql,
            new SugarParameter("@HardwareId", hardwareId));

        return record;
    }

    public async Task<DeviceRegistrationRecord?> FindByDeviceCodeAsync(
        string deviceCode,
        string storeCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [系统设备编号] AS DeviceCode,
                [分店代码] AS StoreCode,
                [设备硬件识别码] AS HardwareId,
                [设备状态] AS DeviceStatus,
                [设备授权码] AS AuthorizationCode
            FROM [POSM_设备注册信息表]
            WHERE [系统设备编号] = @DeviceCode
              AND [分店代码] = @StoreCode;
            """;

        var record = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<DeviceRegistrationRecord>(
            sql,
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@StoreCode", storeCode));

        return record;
    }

    public async Task<DeviceRegistrationRecord?> FindActiveOrLockedRegistrationAsync(
        string hardwareId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [系统设备编号] AS DeviceCode,
                [分店代码] AS StoreCode,
                [设备硬件识别码] AS HardwareId,
                [设备状态] AS DeviceStatus,
                [设备授权码] AS AuthorizationCode
            FROM [POSM_设备注册信息表]
            WHERE [设备硬件识别码] = @HardwareId
              AND [设备状态] IN (1, 2)
            ORDER BY [ID] DESC;
            """;

        var record = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<DeviceRegistrationRecord>(
            sql,
            new SugarParameter("@HardwareId", hardwareId));
        return record;
    }

    public Task<int> DisablePendingRegistrationAsync(
        DeviceRegistrationDisableRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [POSM_设备注册信息表]
            SET [设备状态] = @DisabledStatus,
                [备注] = CONCAT(ISNULL([备注], ''), @RemarkSuffix)
            WHERE [系统设备编号] = @DeviceCode
              AND [分店代码] = @StoreCode
              AND [设备硬件识别码] = @HardwareId
              AND [设备状态] = @PendingStatus;
            """;

        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@DisabledStatus", 0),
            new SugarParameter("@RemarkSuffix", request.RemarkSuffix),
            new SugarParameter("@DeviceCode", request.DeviceCode),
            new SugarParameter("@StoreCode", request.StoreCode),
            new SugarParameter("@HardwareId", request.HardwareId),
            new SugarParameter("@PendingStatus", -1));
    }

    public Task CreateRegistrationAsync(
        DeviceRegistrationCreateRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [POSM_设备注册信息表]
                ([设备硬件识别码], [系统设备编号], [分店代码], [设备类型], [设备系统], [设备状态], [设备授权码], [备注], [创建时间], [创建人])
            VALUES
                (@HardwareId, @DeviceCode, @StoreCode, @DeviceType, @DeviceSystem, @DeviceStatus, @AuthorizationCode, @Remark, @CreatedAt, @CreatedBy);
            """;

        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@HardwareId", request.HardwareId),
            new SugarParameter("@DeviceCode", request.DeviceCode),
            new SugarParameter("@StoreCode", request.StoreCode),
            new SugarParameter("@DeviceType", request.DeviceType),
            new SugarParameter("@DeviceSystem", request.DeviceSystem),
            new SugarParameter("@DeviceStatus", request.DeviceStatus),
            new SugarParameter("@AuthorizationCode", request.AuthorizationCode),
            new SugarParameter("@Remark", request.Remark),
            new SugarParameter("@CreatedAt", request.CreatedAt),
            new SugarParameter("@CreatedBy", request.CreatedBy));
    }

    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await dbContext.PosmDb.Ado.BeginTranAsync();
        try
        {
            await action(cancellationToken);
            await dbContext.PosmDb.Ado.CommitTranAsync();
        }
        catch
        {
            await dbContext.PosmDb.Ado.RollbackTranAsync();
            throw;
        }
    }
}
