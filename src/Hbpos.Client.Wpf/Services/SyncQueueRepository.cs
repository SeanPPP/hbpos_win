using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public interface ISyncQueueRepository
{
    Task<int> CountPendingAsync(CancellationToken cancellationToken = default);

    Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default);
}

public sealed class SyncQueueRepository(LocalSqliteStore store) : ISyncQueueRepository
{
    public async Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SyncQueue WHERE Status = 'Pending';";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN Status = 'Pending' THEN 1 ELSE 0 END) AS PendingCount,
                SUM(CASE WHEN Status = 'Failed' THEN 1 ELSE 0 END) AS FailedCount,
                SUM(CASE WHEN Status = 'Syncing' THEN 1 ELSE 0 END) AS SyncingCount,
                (
                    SELECT ErrorMessage
                    FROM SyncQueue
                    WHERE ErrorMessage IS NOT NULL AND TRIM(ErrorMessage) <> ''
                    ORDER BY COALESCE(LastTriedAt, CreatedAt) DESC
                    LIMIT 1
                ) AS LastError
            FROM SyncQueue;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SyncQueueOverview(0, 0, 0, null);
        }

        return new SyncQueueOverview(
            ReadInt(reader, "PendingCount"),
            ReadInt(reader, "FailedCount"),
            ReadInt(reader, "SyncingCount"),
            ReadNullableString(reader, "LastError"));
    }

    public async Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                q.EntityId,
                q.EntityType,
                q.Status,
                q.CreatedAt,
                q.LastTriedAt,
                q.ErrorMessage,
                o.ActualAmount
            FROM SyncQueue q
            LEFT JOIN LocalOrders o ON o.OrderGuid = q.EntityId
            WHERE q.Status IN ('Pending', 'Failed', 'Syncing')
            ORDER BY q.CreatedAt DESC
            LIMIT $Take;
            """;
        command.Parameters.AddWithValue("$Take", Math.Clamp(take, 1, 100));

        var items = new List<SyncQueueListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SyncQueueListItem(
                Guid.Parse(ReadString(reader, "EntityId")),
                ReadString(reader, "EntityType"),
                ReadString(reader, "Status"),
                ReadDateTimeOffset(reader, "CreatedAt"),
                ReadNullableDateTimeOffset(reader, "LastTriedAt"),
                ReadNullableString(reader, "ErrorMessage"),
                ReadNullableDecimal(reader, "ActualAmount")));
        }

        return items;
    }

    private static int ReadInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
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

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
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

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }
}
