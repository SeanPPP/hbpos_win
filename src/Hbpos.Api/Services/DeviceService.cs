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

public sealed record DeviceReregisterContext(
    string DeviceCode,
    string StoreCode,
    string HardwareId);

public sealed class DeviceService(HbposSqlSugarContext dbContext) : IDeviceService
{
    private const int PendingStatus = -1;
    private const int DisabledStatus = 0;
    private const int EnabledStatus = 1;
    private const int LockedStatus = 2;
    private const int UnregisteredStatus = 3;

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

        var store = await dbContext.MainDb.Queryable<BlazorApp.Shared.Models.Store>()
            .FirstAsync(x => x.StoreCode == storeCode && x.IsActive && !x.IsDeleted, cancellationToken);

        if (store is null)
        {
            return CreateRegisterResponse(string.Empty, storeCode, string.Empty, UnregisteredStatus, "Store was not found or inactive.");
        }

        var existing = await FindDeviceByHardwareIdAsync(hardwareId);
        if (existing is not null)
        {
            if (!string.Equals(existing.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
            {
                return CreateRegisterResponse(
                    existing.DeviceCode ?? string.Empty,
                    existing.StoreCode ?? storeCode,
                    store.StoreName,
                    existing.DeviceStatus,
                    "Device hardware is already registered to another store.");
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

        var deviceCode = CreateDeviceCode(storeCode, DateTime.Now);
        var authorizationCode = Guid.NewGuid().ToString("N");
        const string sql = """
            INSERT INTO [POSM_设备注册信息表]
                ([设备硬件识别码], [系统设备编号], [分店代码], [设备类型], [设备系统], [设备状态], [设备授权码], [备注], [创建时间], [创建人])
            VALUES
                (@HardwareId, @DeviceCode, @StoreCode, @DeviceType, @DeviceSystem, @DeviceStatus, @AuthorizationCode, @Remark, @CreatedAt, @CreatedBy);
            """;

        var parameters = new[]
        {
            new SugarParameter("@HardwareId", hardwareId),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceType", "POS"),
            new SugarParameter("@DeviceSystem", "Windows"),
            new SugarParameter("@DeviceStatus", PendingStatus),
            new SugarParameter("@AuthorizationCode", authorizationCode),
            new SugarParameter("@Remark", string.IsNullOrWhiteSpace(terminalName) ? "HBPOS client registration" : $"HBPOS client registration: {terminalName}"),
            new SugarParameter("@CreatedAt", DateTime.Now),
            new SugarParameter("@CreatedBy", "HBPOS_CLIENT")
        };

        await dbContext.PosmDb.Ado.ExecuteCommandAsync(sql, parameters);

        return new DeviceRegisterResponse(
            deviceCode,
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

        var store = await dbContext.MainDb.Queryable<BlazorApp.Shared.Models.Store>()
            .FirstAsync(x => x.StoreCode == storeCode && x.IsActive && !x.IsDeleted, cancellationToken);

        if (store is null)
        {
            return CreateVerifyResponse(deviceCode, storeCode, string.Empty, UnregisteredStatus, "Store was not found or inactive.");
        }

        var device = await FindDeviceByDeviceCodeAsync(deviceCode, storeCode);
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

        var store = await dbContext.MainDb.Queryable<BlazorApp.Shared.Models.Store>()
            .FirstAsync(x => x.StoreCode == targetStoreCode && x.IsActive && !x.IsDeleted, cancellationToken);

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

        await dbContext.PosmDb.Ado.BeginTranAsync();
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

            await dbContext.PosmDb.Ado.ExecuteCommandAsync(
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

            await dbContext.PosmDb.Ado.ExecuteCommandAsync(
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

            await dbContext.PosmDb.Ado.CommitTranAsync();
        }
        catch
        {
            await dbContext.PosmDb.Ado.RollbackTranAsync();
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

    private async Task<DeviceRegistrationRow?> FindDeviceByHardwareIdAsync(string hardwareId)
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

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<DeviceRegistrationRow>(
            sql,
            new SugarParameter("@HardwareId", hardwareId));
    }

    private async Task<DeviceRegistrationRow?> FindDeviceByDeviceCodeAsync(string deviceCode, string storeCode)
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

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<DeviceRegistrationRow>(
            sql,
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@StoreCode", storeCode));
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

    internal static string CreateDeviceCode(string storeCode, DateTime localTime)
    {
        return $"POS_{storeCode}_{localTime:HHmm}";
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

    private sealed class DeviceRegistrationRow
    {
        public string? DeviceCode { get; set; }

        public string? StoreCode { get; set; }

        public string? HardwareId { get; set; }

        public int DeviceStatus { get; set; }

        public string? AuthorizationCode { get; set; }
    }
}
