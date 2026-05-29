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
                (OrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SoldAt, TotalAmount, DiscountAmount, ActualAmount, TenderedAmount, ChangeAmount, SyncStatus)
                VALUES ($OrderGuid, $StoreCode, $DeviceCode, $CashierId, $CashierName, $SoldAt, $TotalAmount, $DiscountAmount, $ActualAmount, $TenderedAmount, $ChangeAmount, 'Pending');
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
            // 收款/找零信息允许为空，保持历史订单与非现金场景兼容。
            command.Parameters.AddWithValue("$TenderedAmount", order.TenderedAmount ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$ChangeAmount", order.ChangeAmount ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var line in order.Lines)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO LocalOrderLines
                (OrderLineGuid, OrderGuid, ProductCode, ReferenceCode, DisplayName, LookupCode, ItemNumber, Quantity, UnitPrice, DiscountAmount, ActualAmount, PriceSource, Kind, ReturnSourceKey, OriginalOrderGuid, OriginalOrderDetailGuid)
                VALUES ($OrderLineGuid, $OrderGuid, $ProductCode, $ReferenceCode, $DisplayName, $LookupCode, $ItemNumber, $Quantity, $UnitPrice, $DiscountAmount, $ActualAmount, $PriceSource, $Kind, $ReturnSourceKey, $OriginalOrderGuid, $OriginalOrderDetailGuid);
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
            command.Parameters.AddWithValue("$Kind", (int)line.Kind);
            command.Parameters.AddWithValue("$ReturnSourceKey", (object?)line.ReturnSourceKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$OriginalOrderGuid", line.OriginalOrderGuid?.ToString() ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$OriginalOrderDetailGuid", line.OriginalOrderDetailGuid?.ToString() ?? (object)DBNull.Value);
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

            foreach (var cardTransaction in payment.CardTransactions ?? [])
            {
                await using var cardCommand = connection.CreateCommand();
                cardCommand.Transaction = transaction;
                cardCommand.CommandText = """
                    INSERT INTO LocalCardTransactions
                    (Id, PaymentGuid, OrderGuid, Processor, TxnRef, AuthCode, CardType, CardBin, MaskedCardNumber, MerchantId, ResponseCode, ResponseText, Stan, BankDateTime, Amount, ReceiptText)
                    VALUES ($Id, $PaymentGuid, $OrderGuid, $Processor, $TxnRef, $AuthCode, $CardType, $CardBin, $MaskedCardNumber, $MerchantId, $ResponseCode, $ResponseText, $Stan, $BankDateTime, $Amount, $ReceiptText);
                    """;
                cardCommand.Parameters.AddWithValue("$Id", Guid.NewGuid().ToString());
                cardCommand.Parameters.AddWithValue("$PaymentGuid", payment.PaymentGuid.ToString());
                cardCommand.Parameters.AddWithValue("$OrderGuid", order.OrderGuid.ToString());
                cardCommand.Parameters.AddWithValue("$Processor", cardTransaction.Processor);
                cardCommand.Parameters.AddWithValue("$TxnRef", (object?)cardTransaction.TxnRef ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$AuthCode", (object?)cardTransaction.AuthCode ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$CardType", (object?)cardTransaction.CardType ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$CardBin", (object?)cardTransaction.CardBin ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$MaskedCardNumber", (object?)cardTransaction.MaskedCardNumber ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$MerchantId", (object?)cardTransaction.MerchantId ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$ResponseCode", (object?)cardTransaction.ResponseCode ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$ResponseText", (object?)cardTransaction.ResponseText ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$Stan", (object?)cardTransaction.Stan ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$BankDateTime", cardTransaction.BankDateTime?.ToString("O") ?? (object)DBNull.Value);
                cardCommand.Parameters.AddWithValue("$Amount", cardTransaction.Amount);
                cardCommand.Parameters.AddWithValue("$ReceiptText", (object?)cardTransaction.ReceiptText ?? DBNull.Value);
                await cardCommand.ExecuteNonQueryAsync(cancellationToken);
            }
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
            SELECT OrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SoldAt, TotalAmount, DiscountAmount, ActualAmount, TenderedAmount, ChangeAmount
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
            [],
            ReadNullableDecimal(reader, "TenderedAmount"),
            ReadNullableDecimal(reader, "ChangeAmount"));
    }

    private static async Task<IReadOnlyList<LocalOrderLine>> ReadOrderLinesAsync(
        SqliteConnection connection,
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OrderLineGuid, ProductCode, ReferenceCode, DisplayName, LookupCode, ItemNumber, Quantity, UnitPrice, DiscountAmount, ActualAmount, PriceSource, Kind, ReturnSourceKey, OriginalOrderGuid, OriginalOrderDetailGuid
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
                (PriceSourceKind)reader.GetInt32(reader.GetOrdinal("PriceSource")),
                (OrderLineKind)reader.GetInt32(reader.GetOrdinal("Kind")),
                ReadNullableString(reader, "ReturnSourceKey"),
                ReadNullableGuid(reader, "OriginalOrderGuid"),
                ReadNullableGuid(reader, "OriginalOrderDetailGuid")));
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

        var paymentRows = new List<(Guid PaymentGuid, PaymentMethodKind Method, decimal Amount, string? Reference)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var paymentGuid = ReadGuid(reader, "PaymentGuid");
            paymentRows.Add((
                paymentGuid,
                (PaymentMethodKind)reader.GetInt32(reader.GetOrdinal("Method")),
                ReadDecimal(reader, "Amount"),
                ReadNullableString(reader, "Reference")));
        }

        var payments = new List<LocalPayment>(paymentRows.Count);
        foreach (var payment in paymentRows)
        {
            payments.Add(new LocalPayment(
                payment.PaymentGuid,
                payment.Method,
                payment.Amount,
                payment.Reference,
                await ReadCardTransactionsAsync(connection, orderGuid, payment.PaymentGuid, cancellationToken)));
        }

        return payments;
    }

    private static async Task<IReadOnlyList<CardTransactionDto>> ReadCardTransactionsAsync(
        SqliteConnection connection,
        Guid orderGuid,
        Guid paymentGuid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Processor, TxnRef, AuthCode, CardType, CardBin, MaskedCardNumber, MerchantId, ResponseCode, ResponseText, Stan, BankDateTime, Amount, ReceiptText
            FROM LocalCardTransactions
            WHERE OrderGuid = $OrderGuid AND PaymentGuid = $PaymentGuid
            ORDER BY rowid;
            """;
        command.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());
        command.Parameters.AddWithValue("$PaymentGuid", paymentGuid.ToString());

        var transactions = new List<CardTransactionDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            transactions.Add(new CardTransactionDto(
                ReadString(reader, "Processor"),
                ReadNullableString(reader, "TxnRef"),
                ReadNullableString(reader, "AuthCode"),
                ReadNullableString(reader, "CardType"),
                ReadNullableInt(reader, "CardBin"),
                ReadNullableString(reader, "MaskedCardNumber"),
                ReadNullableString(reader, "MerchantId"),
                ReadNullableString(reader, "ResponseCode"),
                ReadNullableString(reader, "ResponseText"),
                ReadNullableString(reader, "Stan"),
                ReadNullableDateTimeOffset(reader, "BankDateTime"),
                ReadDecimal(reader, "Amount"),
                ReadNullableString(reader, "ReceiptText")));
        }

        return transactions;
    }

    private static Guid ReadGuid(SqliteDataReader reader, string name)
    {
        return Guid.Parse(ReadString(reader, name));
    }

    private static Guid? ReadNullableGuid(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value) ? null : Guid.Parse(value);
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

    private static int? ReadNullableInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
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
        return reader.IsDBNull(ordinal) ? null : ReadDecimal(reader, name);
    }

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, string name)
    {
        return DateTimeOffset.Parse(ReadString(reader, name));
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value) ? null : DateTimeOffset.Parse(value);
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
