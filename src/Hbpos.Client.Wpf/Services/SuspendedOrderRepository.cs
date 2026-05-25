using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public interface ISuspendedOrderRepository
{
    Task SaveAsync(SuspendedOrder order, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SuspendedOrderSummary>> GetPendingAsync(
        string storeCode,
        string? deviceCode = null,
        string? keyword = null,
        int take = 100,
        CancellationToken cancellationToken = default);

    Task<SuspendedOrder?> GetAsync(Guid suspendedOrderGuid, CancellationToken cancellationToken = default);

    Task MarkStatusAsync(
        Guid suspendedOrderGuid,
        SuspendedOrderStatus status,
        CancellationToken cancellationToken = default);
}

public sealed class SuspendedOrderRepository(LocalSqliteStore store) : ISuspendedOrderRepository
{
    public async Task SaveAsync(SuspendedOrder order, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO SuspendedOrders
                (SuspendedOrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SuspendedAt, TotalAmount, DiscountAmount, ActualAmount, Status)
                VALUES ($SuspendedOrderGuid, $StoreCode, $DeviceCode, $CashierId, $CashierName, $SuspendedAt, $TotalAmount, $DiscountAmount, $ActualAmount, $Status);
                """;
            command.Parameters.AddWithValue("$SuspendedOrderGuid", order.SuspendedOrderGuid.ToString());
            command.Parameters.AddWithValue("$StoreCode", order.StoreCode);
            command.Parameters.AddWithValue("$DeviceCode", order.DeviceCode);
            command.Parameters.AddWithValue("$CashierId", order.CashierId);
            command.Parameters.AddWithValue("$CashierName", order.CashierName);
            command.Parameters.AddWithValue("$SuspendedAt", order.SuspendedAt.ToString("O"));
            command.Parameters.AddWithValue("$TotalAmount", order.TotalAmount);
            command.Parameters.AddWithValue("$DiscountAmount", order.DiscountAmount);
            command.Parameters.AddWithValue("$ActualAmount", order.ActualAmount);
            command.Parameters.AddWithValue("$Status", (int)order.Status);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var line in order.Lines)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO SuspendedOrderLines
                (SuspendedOrderLineGuid, SuspendedOrderGuid, StoreCode, ProductCode, ReferenceCode, DisplayName, LookupCode, ItemNumber, ProductImage, Quantity, UnitPrice, DiscountAmount, DiscountPercent, ActualAmount, PriceSource, PriceSourceLabel)
                VALUES ($SuspendedOrderLineGuid, $SuspendedOrderGuid, $StoreCode, $ProductCode, $ReferenceCode, $DisplayName, $LookupCode, $ItemNumber, $ProductImage, $Quantity, $UnitPrice, $DiscountAmount, $DiscountPercent, $ActualAmount, $PriceSource, $PriceSourceLabel);
                """;
            command.Parameters.AddWithValue("$SuspendedOrderLineGuid", line.SuspendedOrderLineGuid.ToString());
            command.Parameters.AddWithValue("$SuspendedOrderGuid", line.SuspendedOrderGuid.ToString());
            command.Parameters.AddWithValue("$StoreCode", line.StoreCode);
            command.Parameters.AddWithValue("$ProductCode", line.ProductCode);
            command.Parameters.AddWithValue("$ReferenceCode", (object?)line.ReferenceCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$DisplayName", line.DisplayName);
            command.Parameters.AddWithValue("$LookupCode", line.LookupCode);
            command.Parameters.AddWithValue("$ItemNumber", (object?)line.ItemNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("$ProductImage", (object?)line.ProductImage ?? DBNull.Value);
            command.Parameters.AddWithValue("$Quantity", line.Quantity);
            command.Parameters.AddWithValue("$UnitPrice", line.UnitPrice);
            command.Parameters.AddWithValue("$DiscountAmount", line.DiscountAmount);
            command.Parameters.AddWithValue("$DiscountPercent", (object?)line.DiscountPercent ?? DBNull.Value);
            command.Parameters.AddWithValue("$ActualAmount", line.ActualAmount);
            command.Parameters.AddWithValue("$PriceSource", (int)line.PriceSource);
            command.Parameters.AddWithValue("$PriceSourceLabel", line.PriceSourceLabel);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SuspendedOrderSummary>> GetPendingAsync(
        string storeCode,
        string? deviceCode = null,
        string? keyword = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var normalizedKeyword = (keyword ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedOrderKeyword = normalizedKeyword.Replace("-", string.Empty);
        command.CommandText = """
            SELECT
                o.SuspendedOrderGuid,
                o.StoreCode,
                o.DeviceCode,
                o.CashierName,
                o.SuspendedAt,
                o.TotalAmount,
                o.DiscountAmount,
                o.ActualAmount,
                o.Status,
                COUNT(l.SuspendedOrderLineGuid) AS LineCount
            FROM SuspendedOrders o
            LEFT JOIN SuspendedOrderLines l ON l.SuspendedOrderGuid = o.SuspendedOrderGuid
            WHERE UPPER(o.StoreCode) = $StoreCode
              AND o.Status = $PendingStatus
              AND ($DeviceCode = '' OR UPPER(o.DeviceCode) = $DeviceCode)
              AND (
                    $KeywordLike = ''
                    OR UPPER(o.SuspendedOrderGuid) LIKE $KeywordLike
                    OR REPLACE(UPPER(o.SuspendedOrderGuid), '-', '') LIKE $NormalizedOrderKeywordLike
                    OR EXISTS (
                        SELECT 1
                        FROM SuspendedOrderLines search
                        WHERE search.SuspendedOrderGuid = o.SuspendedOrderGuid
                          AND (
                                UPPER(search.LookupCode) LIKE $KeywordLike
                                OR UPPER(COALESCE(search.ItemNumber, '')) LIKE $KeywordLike
                              )
                    )
                  )
            GROUP BY o.SuspendedOrderGuid
            ORDER BY o.SuspendedAt DESC
            LIMIT $Take;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$PendingStatus", (int)SuspendedOrderStatus.Pending);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode?.Trim().ToUpperInvariant() ?? string.Empty);
        command.Parameters.AddWithValue("$KeywordLike", string.IsNullOrEmpty(normalizedKeyword) ? string.Empty : $"%{normalizedKeyword}%");
        command.Parameters.AddWithValue(
            "$NormalizedOrderKeywordLike",
            string.IsNullOrEmpty(normalizedOrderKeyword) ? string.Empty : $"%{normalizedOrderKeyword}%");
        command.Parameters.AddWithValue("$Take", Math.Clamp(take, 1, 500));

        var result = new List<SuspendedOrderSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SuspendedOrderSummary(
                ReadGuid(reader, "SuspendedOrderGuid"),
                ReadString(reader, "StoreCode"),
                ReadString(reader, "DeviceCode"),
                ReadString(reader, "CashierName"),
                ReadDateTimeOffset(reader, "SuspendedAt"),
                ReadDecimal(reader, "TotalAmount"),
                ReadDecimal(reader, "DiscountAmount"),
                ReadDecimal(reader, "ActualAmount"),
                reader.GetInt32(reader.GetOrdinal("LineCount")),
                (SuspendedOrderStatus)reader.GetInt32(reader.GetOrdinal("Status"))));
        }

        return result;
    }

    public async Task<SuspendedOrder?> GetAsync(Guid suspendedOrderGuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        var header = await ReadHeaderAsync(connection, suspendedOrderGuid, cancellationToken);
        if (header is null)
        {
            return null;
        }

        var lines = await ReadLinesAsync(connection, suspendedOrderGuid, cancellationToken);
        return header with { Lines = lines };
    }

    public async Task MarkStatusAsync(
        Guid suspendedOrderGuid,
        SuspendedOrderStatus status,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SuspendedOrders
            SET Status = $Status
            WHERE SuspendedOrderGuid = $SuspendedOrderGuid;
            """;
        command.Parameters.AddWithValue("$Status", (int)status);
        command.Parameters.AddWithValue("$SuspendedOrderGuid", suspendedOrderGuid.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SuspendedOrder?> ReadHeaderAsync(
        SqliteConnection connection,
        Guid suspendedOrderGuid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SuspendedOrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SuspendedAt, TotalAmount, DiscountAmount, ActualAmount, Status
            FROM SuspendedOrders
            WHERE SuspendedOrderGuid = $SuspendedOrderGuid;
            """;
        command.Parameters.AddWithValue("$SuspendedOrderGuid", suspendedOrderGuid.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SuspendedOrder(
            ReadGuid(reader, "SuspendedOrderGuid"),
            ReadString(reader, "StoreCode"),
            ReadString(reader, "DeviceCode"),
            ReadString(reader, "CashierId"),
            ReadString(reader, "CashierName"),
            ReadDateTimeOffset(reader, "SuspendedAt"),
            ReadDecimal(reader, "TotalAmount"),
            ReadDecimal(reader, "DiscountAmount"),
            ReadDecimal(reader, "ActualAmount"),
            (SuspendedOrderStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            []);
    }

    private static async Task<IReadOnlyList<SuspendedOrderLine>> ReadLinesAsync(
        SqliteConnection connection,
        Guid suspendedOrderGuid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SuspendedOrderLineGuid, SuspendedOrderGuid, StoreCode, ProductCode, ReferenceCode, DisplayName, LookupCode, ItemNumber, ProductImage, Quantity, UnitPrice, DiscountAmount, DiscountPercent, ActualAmount, PriceSource, PriceSourceLabel
            FROM SuspendedOrderLines
            WHERE SuspendedOrderGuid = $SuspendedOrderGuid
            ORDER BY rowid;
            """;
        command.Parameters.AddWithValue("$SuspendedOrderGuid", suspendedOrderGuid.ToString());

        var lines = new List<SuspendedOrderLine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new SuspendedOrderLine(
                ReadGuid(reader, "SuspendedOrderLineGuid"),
                ReadGuid(reader, "SuspendedOrderGuid"),
                ReadString(reader, "StoreCode"),
                ReadString(reader, "ProductCode"),
                ReadNullableString(reader, "ReferenceCode"),
                ReadString(reader, "DisplayName"),
                ReadString(reader, "LookupCode"),
                ReadNullableString(reader, "ItemNumber"),
                ReadNullableString(reader, "ProductImage"),
                ReadDecimal(reader, "Quantity"),
                ReadDecimal(reader, "UnitPrice"),
                ReadDecimal(reader, "DiscountAmount"),
                ReadNullableDecimal(reader, "DiscountPercent"),
                ReadDecimal(reader, "ActualAmount"),
                (PriceSourceKind)reader.GetInt32(reader.GetOrdinal("PriceSource")),
                ReadString(reader, "PriceSourceLabel")));
        }

        return lines;
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

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return ReadDecimal(reader, name);
    }

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, string name)
    {
        return DateTimeOffset.Parse(ReadString(reader, name), CultureInfo.InvariantCulture);
    }
}
