using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Wpf.Services;

public interface ILocalDeviceRepository
{
    Task<LocalDeviceCache?> GetLatestAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DeviceRegisterResponse response, string hardwareId, CancellationToken cancellationToken = default);

    Task SaveAsync(DeviceVerifyResponse response, string hardwareId, CancellationToken cancellationToken = default);

    Task SaveAsync(DeviceReregisterResponse response, string hardwareId, CancellationToken cancellationToken = default);
}

public sealed class LocalDeviceRepository(
    LocalSqliteStore store,
    IDeviceAuthorizationProtector authorizationProtector) : ILocalDeviceRepository
{
    public async Task<LocalDeviceCache?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DeviceCode, StoreCode, StoreName, HardwareId, DeviceStatus, IsAllowed, Message, AuthorizationCodeProtected, UpdatedAt
            FROM DeviceCache
            ORDER BY UpdatedAt DESC
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LocalDeviceCache(
            ReadString(reader, "DeviceCode"),
            ReadString(reader, "StoreCode"),
            ReadString(reader, "StoreName"),
            ReadString(reader, "HardwareId"),
            ReadInt(reader, "DeviceStatus"),
            ReadInt(reader, "IsAllowed") == 1,
            ReadNullableString(reader, "Message"),
            ReadDateTimeOffset(reader, "UpdatedAt"),
            authorizationProtector.Unprotect(ReadNullableString(reader, "AuthorizationCodeProtected")));
    }

    public Task SaveAsync(DeviceRegisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
    {
        return SaveAsync(
            response.DeviceCode,
            response.StoreCode,
            response.StoreName,
            hardwareId,
            response.DeviceStatus,
            response.IsAllowed,
            response.Message,
            response.AuthorizationCode,
            cancellationToken);
    }

    public Task SaveAsync(DeviceVerifyResponse response, string hardwareId, CancellationToken cancellationToken = default)
    {
        return SaveAsync(
            response.DeviceCode,
            response.StoreCode,
            response.StoreName,
            hardwareId,
            response.DeviceStatus,
            response.IsAllowed,
            response.Message,
            response.AuthorizationCode,
            cancellationToken);
    }

    public Task SaveAsync(DeviceReregisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
    {
        return SaveAsync(
            response.DeviceCode,
            response.StoreCode,
            response.StoreName,
            hardwareId,
            response.DeviceStatus,
            response.IsAllowed,
            response.Message,
            response.AuthorizationCode,
            cancellationToken);
    }

    private async Task SaveAsync(
        string deviceCode,
        string storeCode,
        string storeName,
        string hardwareId,
        int deviceStatus,
        bool isAllowed,
        string? message,
        string? authorizationCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO DeviceCache (DeviceCode, StoreCode, StoreName, HardwareId, DeviceStatus, IsAllowed, Message, AuthorizationCodeProtected, UpdatedAt)
            VALUES ($DeviceCode, $StoreCode, $StoreName, $HardwareId, $DeviceStatus, $IsAllowed, $Message, $AuthorizationCodeProtected, $UpdatedAt)
            ON CONFLICT(DeviceCode) DO UPDATE SET
                StoreCode = excluded.StoreCode,
                StoreName = excluded.StoreName,
                HardwareId = excluded.HardwareId,
                DeviceStatus = excluded.DeviceStatus,
                IsAllowed = excluded.IsAllowed,
                Message = excluded.Message,
                AuthorizationCodeProtected = excluded.AuthorizationCodeProtected,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$StoreName", storeName);
        command.Parameters.AddWithValue("$HardwareId", hardwareId);
        command.Parameters.AddWithValue("$DeviceStatus", deviceStatus);
        command.Parameters.AddWithValue("$IsAllowed", isAllowed ? 1 : 0);
        command.Parameters.AddWithValue("$Message", (object?)message ?? DBNull.Value);
        command.Parameters.AddWithValue("$AuthorizationCodeProtected", (object?)authorizationProtector.Protect(authorizationCode) ?? DBNull.Value);
        command.Parameters.AddWithValue("$UpdatedAt", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ReadString(Microsoft.Data.Sqlite.SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static string? ReadNullableString(Microsoft.Data.Sqlite.SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int ReadInt(Microsoft.Data.Sqlite.SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
    }

    private static DateTimeOffset ReadDateTimeOffset(Microsoft.Data.Sqlite.SqliteDataReader reader, string name)
    {
        var raw = ReadString(reader, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : DateTimeOffset.MinValue;
    }
}
