using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Hbpos.Client.Wpf.Services;

public interface ILocalOrderRepository
{
    Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
        LocalOrderHistoryQuery query,
        int take = 50,
        CancellationToken cancellationToken = default);

    Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default);
}

public sealed class LocalOrderRepository(LocalSqliteStore store) : ILocalOrderRepository
{
    public async Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO LocalOrders
                (OrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SoldAt, TotalAmount, DiscountAmount, ActualAmount, SyncStatus)
                VALUES ($OrderGuid, $StoreCode, $DeviceCode, $CashierId, $CashierName, $SoldAt, $TotalAmount, $DiscountAmount, $ActualAmount, 'Pending');
                """;
            command.Parameters.AddWithValue("$OrderGuid", order.OrderGuid.ToString());
            command.Parameters.AddWithValue("$StoreCode", order.StoreCode);
            command.Parameters.AddWithValue("$DeviceCode", order.DeviceCode);
            command.Parameters.AddWithValue("$CashierId", order.CashierId);
            command.Parameters.AddWithValue("$CashierName", order.CashierName);
            command.Parameters.AddWithValue("$SoldAt", order.SoldAt.ToString("O"));
            command.Parameters.AddWithValue("$TotalAmount", order.TotalAmount);
            command.Parameters.AddWithValue("$DiscountAmount", order.DiscountAmount);
            command.Parameters.AddWithValue("$ActualAmount", order.ActualAmount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var line in order.Lines)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO LocalOrderLines
                (OrderLineGuid, OrderGuid, ProductCode, ReferenceCode, DisplayName, LookupCode, ItemNumber, Quantity, UnitPrice, DiscountAmount, ActualAmount, PriceSource)
                VALUES ($OrderLineGuid, $OrderGuid, $ProductCode, $ReferenceCode, $DisplayName, $LookupCode, $ItemNumber, $Quantity, $UnitPrice, $DiscountAmount, $ActualAmount, $PriceSource);
                """;
            command.Parameters.AddWithValue("$OrderLineGuid", line.OrderLineGuid.ToString());
            command.Parameters.AddWithValue("$OrderGuid", order.OrderGuid.ToString());
            command.Parameters.AddWithValue("$ProductCode", line.ProductCode);
            command.Parameters.AddWithValue("$ReferenceCode", (object?)line.ReferenceCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$DisplayName", line.DisplayName);
            command.Parameters.AddWithValue("$LookupCode", line.LookupCode);
            command.Parameters.AddWithValue("$ItemNumber", (object?)line.ItemNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("$Quantity", line.Quantity);
            command.Parameters.AddWithValue("$UnitPrice", line.UnitPrice);
            command.Parameters.AddWithValue("$DiscountAmount", line.DiscountAmount);
            command.Parameters.AddWithValue("$ActualAmount", line.ActualAmount);
            command.Parameters.AddWithValue("$PriceSource", (int)line.PriceSource);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var payment in order.Payments)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO LocalPayments (PaymentGuid, OrderGuid, Method, Amount, Reference)
                VALUES ($PaymentGuid, $OrderGuid, $Method, $Amount, $Reference);
                """;
            command.Parameters.AddWithValue("$PaymentGuid", payment.PaymentGuid.ToString());
            command.Parameters.AddWithValue("$OrderGuid", order.OrderGuid.ToString());
            command.Parameters.AddWithValue("$Method", (int)payment.Method);
            command.Parameters.AddWithValue("$Amount", payment.Amount);
            command.Parameters.AddWithValue("$Reference", (object?)payment.Reference ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var queue = connection.CreateCommand())
        {
            queue.Transaction = transaction;
            queue.CommandText = """
                INSERT INTO SyncQueue (EntityId, EntityType, Status, CreatedAt)
                VALUES ($EntityId, 'Order', 'Pending', $CreatedAt);
                """;
            queue.Parameters.AddWithValue("$EntityId", order.OrderGuid.ToString());
            queue.Parameters.AddWithValue("$CreatedAt", DateTimeOffset.Now.ToString("O"));
            await queue.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        return await GetRecentOrdersAsync(new LocalOrderHistoryQuery(), take, cancellationToken);
    }

    public async Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
        LocalOrderHistoryQuery query,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var keyword = query.Keyword?.Trim() ?? string.Empty;
        var normalizedKeyword = keyword.ToUpperInvariant();
        var normalizedOrderKeyword = normalizedKeyword.Replace("-", string.Empty);
        command.CommandText = """
            SELECT
                o.OrderGuid,
                o.StoreCode,
                o.DeviceCode,
                o.CashierName,
                o.SoldAt,
                o.TotalAmount,
                o.DiscountAmount,
                o.ActualAmount,
                o.SyncStatus,
                COUNT(DISTINCT l.OrderLineGuid) AS LineCount,
                COALESCE(GROUP_CONCAT(DISTINCT p.Method), '') AS PaymentMethods
            FROM LocalOrders o
            LEFT JOIN LocalOrderLines l ON l.OrderGuid = o.OrderGuid
            LEFT JOIN LocalPayments p ON p.OrderGuid = o.OrderGuid
            WHERE ($SoldFrom IS NULL OR julianday(o.SoldAt) >= julianday($SoldFrom))
              AND ($SoldTo IS NULL OR julianday(o.SoldAt) <= julianday($SoldTo))
              AND ($DeviceCode = '' OR UPPER(o.DeviceCode) = $DeviceCode)
              AND (
                    $KeywordLike = ''
                    OR UPPER(o.OrderGuid) LIKE $KeywordLike
                    OR REPLACE(UPPER(o.OrderGuid), '-', '') LIKE $NormalizedOrderKeywordLike
                    OR EXISTS (
                        SELECT 1
                        FROM LocalOrderLines search
                        WHERE search.OrderGuid = o.OrderGuid
                          AND (
                                UPPER(search.LookupCode) LIKE $KeywordLike
                                OR UPPER(COALESCE(search.ItemNumber, '')) LIKE $KeywordLike
                              )
                    )
                  )
            GROUP BY o.OrderGuid
            ORDER BY o.SoldAt DESC
            LIMIT $Take;
            """;
        command.Parameters.AddWithValue("$SoldFrom", query.SoldFrom?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$SoldTo", query.SoldTo?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$DeviceCode", query.DeviceCode?.Trim().ToUpperInvariant() ?? string.Empty);
        command.Parameters.AddWithValue("$KeywordLike", string.IsNullOrEmpty(normalizedKeyword) ? string.Empty : $"%{normalizedKeyword}%");
        command.Parameters.AddWithValue(
            "$NormalizedOrderKeywordLike",
            string.IsNullOrEmpty(normalizedOrderKeyword) ? string.Empty : $"%{normalizedOrderKeyword}%");
        command.Parameters.AddWithValue("$Take", Math.Clamp(take, 1, 500));

        var summaries = new List<LocalOrderSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new LocalOrderSummary(
                ReadGuid(reader, "OrderGuid"),
                ReadString(reader, "StoreCode"),
                ReadString(reader, "DeviceCode"),
                ReadString(reader, "CashierName"),
                ReadDateTimeOffset(reader, "SoldAt"),
                ReadDecimal(reader, "TotalAmount"),
                ReadDecimal(reader, "DiscountAmount"),
                ReadDecimal(reader, "ActualAmount"),
                ReadString(reader, "SyncStatus"),
                reader.GetInt32(reader.GetOrdinal("LineCount")),
                FormatPaymentSummary(ReadString(reader, "PaymentMethods"))));
        }

        return summaries;
    }

    public async Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        var header = await ReadOrderHeaderAsync(connection, orderGuid, cancellationToken);
        if (header is null)
        {
            return null;
        }

        var lines = await ReadOrderLinesAsync(connection, orderGuid, cancellationToken);
        var payments = await ReadPaymentsAsync(connection, orderGuid, cancellationToken);

        return header with
        {
            Lines = lines,
            Payments = payments
        };
    }

    private static async Task<LocalOrder?> ReadOrderHeaderAsync(
        SqliteConnection connection,
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SoldAt, TotalAmount, DiscountAmount, ActualAmount
            FROM LocalOrders
            WHERE OrderGuid = $OrderGuid;
            """;
        command.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LocalOrder(
            ReadGuid(reader, "OrderGuid"),
            ReadString(reader, "StoreCode"),
            ReadString(reader, "DeviceCode"),
            ReadString(reader, "CashierId"),
            ReadString(reader, "CashierName"),
            ReadDateTimeOffset(reader, "SoldAt"),
            ReadDecimal(reader, "TotalAmount"),
            ReadDecimal(reader, "DiscountAmount"),
            ReadDecimal(reader, "ActualAmount"),
            [],
            []);
    }

    private static async Task<IReadOnlyList<LocalOrderLine>> ReadOrderLinesAsync(
        SqliteConnection connection,
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OrderLineGuid, ProductCode, ReferenceCode, DisplayName, LookupCode, ItemNumber, Quantity, UnitPrice, DiscountAmount, ActualAmount, PriceSource
            FROM LocalOrderLines
            WHERE OrderGuid = $OrderGuid
            ORDER BY rowid;
            """;
        command.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());

        var lines = new List<LocalOrderLine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new LocalOrderLine(
                ReadGuid(reader, "OrderLineGuid"),
                ReadString(reader, "ProductCode"),
                ReadNullableString(reader, "ReferenceCode"),
                ReadString(reader, "DisplayName"),
                ReadString(reader, "LookupCode"),
                ReadNullableString(reader, "ItemNumber"),
                ReadDecimal(reader, "Quantity"),
                ReadDecimal(reader, "UnitPrice"),
                ReadDecimal(reader, "DiscountAmount"),
                ReadDecimal(reader, "ActualAmount"),
                (PriceSourceKind)reader.GetInt32(reader.GetOrdinal("PriceSource"))));
        }

        return lines;
    }

    private static async Task<IReadOnlyList<LocalPayment>> ReadPaymentsAsync(
        SqliteConnection connection,
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PaymentGuid, Method, Amount, Reference
            FROM LocalPayments
            WHERE OrderGuid = $OrderGuid
            ORDER BY rowid;
            """;
        command.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());

        var payments = new List<LocalPayment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            payments.Add(new LocalPayment(
                ReadGuid(reader, "PaymentGuid"),
                (PaymentMethodKind)reader.GetInt32(reader.GetOrdinal("Method")),
                ReadDecimal(reader, "Amount"),
                ReadNullableString(reader, "Reference")));
        }

        return payments;
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
        return DateTimeOffset.Parse(ReadString(reader, name));
    }

    private static string FormatPaymentSummary(string methodList)
    {
        if (string.IsNullOrWhiteSpace(methodList))
        {
            return "None";
        }

        return string.Join(", ", methodList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Enum.TryParse<PaymentMethodKind>(value, out var method) ? method.ToString() : value)
            .Distinct());
    }
}
