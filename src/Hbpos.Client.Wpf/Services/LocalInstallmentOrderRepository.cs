using System.Globalization;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Installments;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public interface ILocalInstallmentOrderRepository
{
    Task UpsertAsync(LocalInstallmentOrder order, CancellationToken cancellationToken = default);

    Task<LocalInstallmentOrder?> GetAsync(Guid installmentGuid, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalInstallmentOrder>> GetRecentByStoreAsync(
        string storeCode,
        int take = 50,
        CancellationToken cancellationToken = default);
}

public sealed class LocalInstallmentOrderRepository(LocalSqliteStore store) : ILocalInstallmentOrderRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task UpsertAsync(LocalInstallmentOrder order, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LocalOrderInstallments
            (
                InstallmentGuid,
                OrderGuid,
                InstallmentNumber,
                StoreCode,
                DeviceCode,
                CashierId,
                CashierName,
                CustomerName,
                CustomerPhone,
                CreatedAt,
                UpdatedAt,
                TotalAmount,
                MinimumDownPayment,
                DownPaymentAmount,
                PaidAmount,
                BalanceAmount,
                Status,
                LinesJson,
                PaymentsJson,
                PickupInfoJson,
                CancellationInfoJson,
                Note
            )
            VALUES
            (
                $InstallmentGuid,
                $OrderGuid,
                $InstallmentNumber,
                $StoreCode,
                $DeviceCode,
                $CashierId,
                $CashierName,
                $CustomerName,
                $CustomerPhone,
                $CreatedAt,
                $UpdatedAt,
                $TotalAmount,
                $MinimumDownPayment,
                $DownPaymentAmount,
                $PaidAmount,
                $BalanceAmount,
                $Status,
                $LinesJson,
                $PaymentsJson,
                $PickupInfoJson,
                $CancellationInfoJson,
                $Note
            )
            ON CONFLICT(InstallmentGuid) DO UPDATE SET
                OrderGuid = excluded.OrderGuid,
                InstallmentNumber = excluded.InstallmentNumber,
                StoreCode = excluded.StoreCode,
                DeviceCode = excluded.DeviceCode,
                CashierId = excluded.CashierId,
                CashierName = excluded.CashierName,
                CustomerName = excluded.CustomerName,
                CustomerPhone = excluded.CustomerPhone,
                CreatedAt = excluded.CreatedAt,
                UpdatedAt = excluded.UpdatedAt,
                TotalAmount = excluded.TotalAmount,
                MinimumDownPayment = excluded.MinimumDownPayment,
                DownPaymentAmount = excluded.DownPaymentAmount,
                PaidAmount = excluded.PaidAmount,
                BalanceAmount = excluded.BalanceAmount,
                Status = excluded.Status,
                LinesJson = excluded.LinesJson,
                PaymentsJson = excluded.PaymentsJson,
                PickupInfoJson = excluded.PickupInfoJson,
                CancellationInfoJson = excluded.CancellationInfoJson,
                Note = excluded.Note;
            """;
        command.Parameters.AddWithValue("$InstallmentGuid", order.InstallmentGuid.ToString());
        command.Parameters.AddWithValue("$OrderGuid", order.OrderGuid.ToString());
        command.Parameters.AddWithValue("$InstallmentNumber", order.InstallmentNumber);
        command.Parameters.AddWithValue("$StoreCode", order.StoreCode);
        command.Parameters.AddWithValue("$DeviceCode", order.DeviceCode);
        command.Parameters.AddWithValue("$CashierId", order.CashierId);
        command.Parameters.AddWithValue("$CashierName", order.CashierName);
        command.Parameters.AddWithValue("$CustomerName", order.CustomerName);
        command.Parameters.AddWithValue("$CustomerPhone", order.CustomerPhone);
        command.Parameters.AddWithValue("$CreatedAt", order.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$UpdatedAt", order.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$TotalAmount", order.TotalAmount);
        command.Parameters.AddWithValue("$MinimumDownPayment", order.MinimumDownPayment);
        command.Parameters.AddWithValue("$DownPaymentAmount", order.DownPaymentAmount);
        command.Parameters.AddWithValue("$PaidAmount", order.PaidAmount);
        command.Parameters.AddWithValue("$BalanceAmount", order.BalanceAmount);
        command.Parameters.AddWithValue("$Status", (int)order.Status);
        // 分期明细与付款历史只保存在专属表中，避免污染本地零售订单表结构。
        command.Parameters.AddWithValue("$LinesJson", JsonSerializer.Serialize(order.Lines, JsonOptions));
        command.Parameters.AddWithValue("$PaymentsJson", JsonSerializer.Serialize(order.Payments, JsonOptions));
        command.Parameters.AddWithValue(
            "$PickupInfoJson",
            order.PickupInfo is null
                ? (object)DBNull.Value
                : JsonSerializer.Serialize(order.PickupInfo, JsonOptions));
        command.Parameters.AddWithValue(
            "$CancellationInfoJson",
            order.CancellationInfo is null
                ? (object)DBNull.Value
                : JsonSerializer.Serialize(order.CancellationInfo, JsonOptions));
        command.Parameters.AddWithValue("$Note", (object?)order.Note ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LocalInstallmentOrder?> GetAsync(Guid installmentGuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                InstallmentGuid,
                OrderGuid,
                InstallmentNumber,
                StoreCode,
                DeviceCode,
                CashierId,
                CashierName,
                CustomerName,
                CustomerPhone,
                CreatedAt,
                UpdatedAt,
                TotalAmount,
                MinimumDownPayment,
                DownPaymentAmount,
                PaidAmount,
                BalanceAmount,
                Status,
                LinesJson,
                PaymentsJson,
                PickupInfoJson,
                CancellationInfoJson,
                Note
            FROM LocalOrderInstallments
            WHERE InstallmentGuid = $InstallmentGuid;
            """;
        command.Parameters.AddWithValue("$InstallmentGuid", installmentGuid.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadOrder(reader)
            : null;
    }

    public async Task<IReadOnlyList<LocalInstallmentOrder>> GetRecentByStoreAsync(
        string storeCode,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                InstallmentGuid,
                OrderGuid,
                InstallmentNumber,
                StoreCode,
                DeviceCode,
                CashierId,
                CashierName,
                CustomerName,
                CustomerPhone,
                CreatedAt,
                UpdatedAt,
                TotalAmount,
                MinimumDownPayment,
                DownPaymentAmount,
                PaidAmount,
                BalanceAmount,
                Status,
                LinesJson,
                PaymentsJson,
                PickupInfoJson,
                CancellationInfoJson,
                Note
            FROM LocalOrderInstallments
            WHERE StoreCode = $StoreCode
            ORDER BY CreatedAt DESC
            LIMIT $Take;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$Take", Math.Clamp(take, 1, 500));

        var orders = new List<LocalInstallmentOrder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            orders.Add(ReadOrder(reader));
        }

        return orders;
    }

    private static LocalInstallmentOrder ReadOrder(SqliteDataReader reader)
    {
        return new LocalInstallmentOrder(
            ReadGuid(reader, "OrderGuid"),
            ReadGuid(reader, "InstallmentGuid"),
            ReadString(reader, "InstallmentNumber"),
            ReadString(reader, "StoreCode"),
            ReadString(reader, "DeviceCode"),
            ReadString(reader, "CashierId"),
            ReadString(reader, "CashierName"),
            ReadString(reader, "CustomerName"),
            ReadString(reader, "CustomerPhone"),
            ReadDateTimeOffset(reader, "CreatedAt"),
            ReadDateTimeOffset(reader, "UpdatedAt"),
            ReadDecimal(reader, "TotalAmount"),
            ReadDecimal(reader, "MinimumDownPayment"),
            ReadDecimal(reader, "DownPaymentAmount"),
            ReadDecimal(reader, "PaidAmount"),
            ReadDecimal(reader, "BalanceAmount"),
            (InstallmentStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            Deserialize<IReadOnlyList<InstallmentLineDto>>(ReadString(reader, "LinesJson")) ?? [],
            Deserialize<IReadOnlyList<InstallmentPaymentDto>>(ReadString(reader, "PaymentsJson")) ?? [],
            Deserialize<InstallmentPickupInfoDto>(ReadNullableString(reader, "PickupInfoJson")),
            ReadNullableString(reader, "Note"),
            Deserialize<InstallmentCancellationInfoDto>(ReadNullableString(reader, "CancellationInfoJson")));
    }

    private static T? Deserialize<T>(string? json)
    {
        return string.IsNullOrWhiteSpace(json)
            ? default
            : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static Guid ReadGuid(SqliteDataReader reader, string name)
    {
        return Guid.Parse(ReadString(reader, name));
    }

    private static string ReadString(SqliteDataReader reader, string name)
    {
        return reader.GetString(reader.GetOrdinal(name));
    }

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static decimal ReadDecimal(SqliteDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value switch
        {
            decimal decimalValue => decimalValue,
            double doubleValue => Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture),
            long longValue => longValue,
            int intValue => intValue,
            string stringValue => decimal.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };
    }

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, string name)
    {
        return DateTimeOffset.Parse(ReadString(reader, name), CultureInfo.InvariantCulture);
    }
}
