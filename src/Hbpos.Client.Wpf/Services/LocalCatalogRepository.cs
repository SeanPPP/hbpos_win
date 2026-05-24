using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hbpos.Contracts.Catalog;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public sealed record LocalSellableItemCompareRow(
    string StoreCode,
    string LookupCodeNormalized,
    string ContentHash,
    DateTimeOffset? SyncedAt);

public interface ILocalCatalogRepository
{
    Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default);

    Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default);

    Task<int> DeleteByLookupCodesAsync(string storeCode, IEnumerable<string> lookupCodes, CancellationToken cancellationToken = default);

    Task<SellableItemDto?> FindByLookupCodeAsync(string storeCode, string lookupCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(string storeCode, CancellationToken cancellationToken = default);

    Task SaveSpecialProductOrderAsync(string storeCode, IEnumerable<string> productCodes, CancellationToken cancellationToken = default);

    Task<int> UpdateSpecialProductFlagAsync(
        string storeCode,
        string productCode,
        bool isSpecialProduct,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(
        string storeCode,
        string? afterLookupCodeNormalized,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalCatalogRepository(LocalSqliteStore store) : ILocalCatalogRepository
{
    public Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
    {
        return UpsertSellableItemsAsync(items, cancellationToken);
    }

    public async Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
    {
        var materializedItems = items.ToList();
        if (materializedItems.Count == 0)
        {
            return;
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var syncedAt = DateTimeOffset.UtcNow;

        foreach (var item in materializedItems)
        {
            var storeCode = NormalizeStoreCode(item.StoreCode);
            var lookupCodeNormalized = NormalizeLookupCode(item.LookupCode);
            if (string.IsNullOrEmpty(storeCode))
            {
                throw new ArgumentException("Sellable item store code is required.", nameof(items));
            }

            if (string.IsNullOrEmpty(lookupCodeNormalized))
            {
                throw new ArgumentException("Sellable item lookup code is required.", nameof(items));
            }

            var contentHash = CreateContentHash(item, storeCode, lookupCodeNormalized);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO LocalSellableItemIndex
                (
                    StoreCode,
                    ProductCode,
                    ReferenceCode,
                    DisplayName,
                    LookupCode,
                    LookupCodeNormalized,
                    ItemNumber,
                    Barcode,
                    ProductImage,
                    DiscountRate,
                    IsSpecialProduct,
                    RetailPrice,
                    PriceSource,
                    PriceSourceLabel,
                    QuantityFactor,
                    UpdatedAt,
                    ContentHash,
                    SyncedAt
                )
                VALUES
                (
                    $StoreCode,
                    $ProductCode,
                    $ReferenceCode,
                    $DisplayName,
                    $LookupCode,
                    $LookupCodeNormalized,
                    $ItemNumber,
                    $Barcode,
                    $ProductImage,
                    $DiscountRate,
                    $IsSpecialProduct,
                    $RetailPrice,
                    $PriceSource,
                    $PriceSourceLabel,
                    $QuantityFactor,
                    $UpdatedAt,
                    $ContentHash,
                    $SyncedAt
                )
                ON CONFLICT(StoreCode, LookupCodeNormalized) DO UPDATE SET
                    ProductCode = excluded.ProductCode,
                    ReferenceCode = excluded.ReferenceCode,
                    DisplayName = excluded.DisplayName,
                    LookupCode = excluded.LookupCode,
                    ItemNumber = excluded.ItemNumber,
                    Barcode = excluded.Barcode,
                    ProductImage = excluded.ProductImage,
                    DiscountRate = excluded.DiscountRate,
                    IsSpecialProduct = excluded.IsSpecialProduct,
                    RetailPrice = excluded.RetailPrice,
                    PriceSource = excluded.PriceSource,
                    PriceSourceLabel = excluded.PriceSourceLabel,
                    QuantityFactor = excluded.QuantityFactor,
                    UpdatedAt = excluded.UpdatedAt,
                    ContentHash = excluded.ContentHash,
                    SyncedAt = excluded.SyncedAt;
                """;

            AddItemParameters(command, item, storeCode, lookupCodeNormalized, contentHash, syncedAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> DeleteByLookupCodesAsync(
        string storeCode,
        IEnumerable<string> lookupCodes,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return 0;
        }

        var normalizedLookupCodes = lookupCodes
            .Select(NormalizeLookupCode)
            .Where(code => !string.IsNullOrEmpty(code))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedLookupCodes.Length == 0)
        {
            return 0;
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var deleted = 0;

        foreach (var lookupCodeNormalized in normalizedLookupCodes)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM LocalSellableItemIndex
                WHERE StoreCode = $StoreCode
                  AND LookupCodeNormalized = $LookupCodeNormalized;
                """;
            command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            command.Parameters.AddWithValue("$LookupCodeNormalized", lookupCodeNormalized);
            deleted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<SellableItemDto?> FindByLookupCodeAsync(
        string storeCode,
        string lookupCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var lookupCodeNormalized = NormalizeLookupCode(lookupCode);
        if (string.IsNullOrEmpty(normalizedStoreCode) || string.IsNullOrEmpty(lookupCodeNormalized))
        {
            return null;
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectSellableItemSql}
            WHERE StoreCode = $StoreCode
              AND LookupCodeNormalized = $LookupCodeNormalized
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
        command.Parameters.AddWithValue("$LookupCodeNormalized", lookupCodeNormalized);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSellableItem(reader) : null;
    }

    public async Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return [];
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                l.StoreCode,
                l.ProductCode,
                l.ReferenceCode,
                l.DisplayName,
                l.LookupCode,
                l.ItemNumber,
                l.Barcode,
                l.ProductImage,
                l.DiscountRate,
                l.IsSpecialProduct,
                l.RetailPrice,
                l.PriceSource,
                l.PriceSourceLabel,
                l.QuantityFactor,
                l.UpdatedAt,
                s.SortOrder
            FROM LocalSellableItemIndex l
            LEFT JOIN LocalSpecialProductSortOrder s
              ON s.StoreCode = l.StoreCode
             AND s.ProductCode = l.ProductCode
            WHERE l.StoreCode = $StoreCode
              AND l.IsSpecialProduct = 1
            ORDER BY
                CASE WHEN s.SortOrder IS NULL THEN 1 ELSE 0 END,
                s.SortOrder,
                l.DisplayName,
                l.ProductCode,
                l.LookupCodeNormalized;
            """;
        command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);

        var rows = new List<(SellableItemDto Item, int? SortOrder)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((ReadSellableItem(reader), ReadNullableInt32(reader, "SortOrder")));
        }

        return rows
            .GroupBy(row => NormalizeProductCode(row.Item.ProductCode), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(row => row.SortOrder ?? int.MaxValue)
                .ThenBy(row => PreferredSpecialLookupRank(row.Item))
                .ThenBy(row => row.Item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Item.LookupCode, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(row => row.SortOrder ?? int.MaxValue)
            .ThenBy(row => row.Item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.Item.ProductCode, StringComparer.OrdinalIgnoreCase)
            .Select(row => row.Item)
            .ToArray();
    }

    public async Task SaveSpecialProductOrderAsync(
        string storeCode,
        IEnumerable<string> productCodes,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return;
        }

        var normalizedProductCodes = productCodes
            .Select(NormalizeProductCode)
            .Where(code => !string.IsNullOrEmpty(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM LocalSpecialProductSortOrder WHERE StoreCode = $StoreCode;";
            deleteCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var updatedAt = DateTimeOffset.UtcNow.ToString("O");
        for (var index = 0; index < normalizedProductCodes.Length; index++)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO LocalSpecialProductSortOrder (StoreCode, ProductCode, SortOrder, UpdatedAt)
                VALUES ($StoreCode, $ProductCode, $SortOrder, $UpdatedAt);
                """;
            insertCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            insertCommand.Parameters.AddWithValue("$ProductCode", normalizedProductCodes[index]);
            insertCommand.Parameters.AddWithValue("$SortOrder", index);
            insertCommand.Parameters.AddWithValue("$UpdatedAt", updatedAt);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> UpdateSpecialProductFlagAsync(
        string storeCode,
        string productCode,
        bool isSpecialProduct,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var normalizedProductCode = NormalizeProductCode(productCode);
        if (string.IsNullOrEmpty(normalizedStoreCode) || string.IsNullOrEmpty(normalizedProductCode))
        {
            return 0;
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE LocalSellableItemIndex
            SET IsSpecialProduct = $IsSpecialProduct
            WHERE StoreCode = $StoreCode
              AND ProductCode = $ProductCode;
            """;
        command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
        command.Parameters.AddWithValue("$ProductCode", normalizedProductCode);
        command.Parameters.AddWithValue("$IsSpecialProduct", isSpecialProduct ? 1 : 0);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);

        if (!isSpecialProduct)
        {
            await using var deleteOrderCommand = connection.CreateCommand();
            deleteOrderCommand.Transaction = transaction;
            deleteOrderCommand.CommandText = """
                DELETE FROM LocalSpecialProductSortOrder
                WHERE StoreCode = $StoreCode
                  AND ProductCode = $ProductCode;
                """;
            deleteOrderCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            deleteOrderCommand.Parameters.AddWithValue("$ProductCode", normalizedProductCode);
            await deleteOrderCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(
        string storeCode,
        string? afterLookupCodeNormalized,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return [];
        }

        var cursor = string.IsNullOrWhiteSpace(afterLookupCodeNormalized)
            ? null
            : NormalizeLookupCode(afterLookupCodeNormalized);

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT StoreCode, LookupCodeNormalized, ContentHash, SyncedAt
            FROM LocalSellableItemIndex
            WHERE StoreCode = $StoreCode
              AND ($AfterLookupCodeNormalized IS NULL OR LookupCodeNormalized > $AfterLookupCodeNormalized)
            ORDER BY StoreCode, LookupCodeNormalized
            LIMIT $PageSize;
            """;
        command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
        command.Parameters.AddWithValue("$AfterLookupCodeNormalized", (object?)cursor ?? DBNull.Value);
        command.Parameters.AddWithValue("$PageSize", Math.Clamp(pageSize, 1, 1000));

        var rows = new List<LocalSellableItemCompareRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LocalSellableItemCompareRow(
                ReadString(reader, "StoreCode"),
                ReadString(reader, "LookupCodeNormalized"),
                ReadString(reader, "ContentHash"),
                ReadNullableDateTimeOffset(reader, "SyncedAt")));
        }

        return rows;
    }

    public async Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectSellableItemSql}
            ORDER BY StoreCode, LookupCodeNormalized;
            """;

        var items = new List<SellableItemDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadSellableItem(reader));
        }

        return items;
    }

    private const string SelectSellableItemSql = """
        SELECT StoreCode, ProductCode, ReferenceCode, DisplayName, LookupCode, ItemNumber, Barcode, ProductImage, DiscountRate, IsSpecialProduct, RetailPrice, PriceSource, PriceSourceLabel, QuantityFactor, UpdatedAt
        FROM LocalSellableItemIndex
        """;

    private static void AddItemParameters(
        SqliteCommand command,
        SellableItemDto item,
        string storeCode,
        string lookupCodeNormalized,
        string contentHash,
        DateTimeOffset syncedAt)
    {
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$ProductCode", item.ProductCode);
        command.Parameters.AddWithValue("$ReferenceCode", (object?)item.ReferenceCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$DisplayName", item.DisplayName);
        command.Parameters.AddWithValue("$LookupCode", item.LookupCode);
        command.Parameters.AddWithValue("$LookupCodeNormalized", lookupCodeNormalized);
        command.Parameters.AddWithValue("$ItemNumber", (object?)item.ItemNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$Barcode", (object?)item.Barcode ?? DBNull.Value);
        command.Parameters.AddWithValue("$ProductImage", (object?)item.ProductImage ?? DBNull.Value);
        command.Parameters.AddWithValue("$DiscountRate", (object?)item.DiscountRate ?? DBNull.Value);
        command.Parameters.AddWithValue("$IsSpecialProduct", item.IsSpecialProduct ? 1 : 0);
        command.Parameters.AddWithValue("$RetailPrice", item.RetailPrice);
        command.Parameters.AddWithValue("$PriceSource", (int)item.PriceSource);
        command.Parameters.AddWithValue("$PriceSourceLabel", item.PriceSourceLabel);
        command.Parameters.AddWithValue("$QuantityFactor", item.QuantityFactor);
        command.Parameters.AddWithValue("$UpdatedAt", (object?)item.UpdatedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$ContentHash", contentHash);
        command.Parameters.AddWithValue("$SyncedAt", syncedAt.ToString("O"));
    }

    private static SellableItemDto ReadSellableItem(SqliteDataReader reader)
    {
        return new SellableItemDto(
            ReadString(reader, "StoreCode"),
            ReadString(reader, "ProductCode"),
            ReadNullableString(reader, "ReferenceCode"),
            ReadString(reader, "DisplayName"),
            ReadString(reader, "LookupCode"),
            ReadNullableString(reader, "ItemNumber"),
            ReadNullableString(reader, "Barcode"),
            ReadDecimal(reader, "RetailPrice"),
            (PriceSourceKind)ReadInt32(reader, "PriceSource"),
            ReadString(reader, "PriceSourceLabel"),
            ReadDecimal(reader, "QuantityFactor"),
            ReadNullableDateTimeOffset(reader, "UpdatedAt"),
            ReadNullableString(reader, "ProductImage"),
            ReadNullableDecimal(reader, "DiscountRate"),
            ReadBool(reader, "IsSpecialProduct"));
    }

    private static string CreateContentHash(SellableItemDto item, string storeCode, string lookupCodeNormalized)
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, storeCode);
        AppendCanonical(builder, item.ProductCode.Trim());
        AppendCanonical(builder, item.ReferenceCode?.Trim() ?? string.Empty);
        AppendCanonical(builder, item.DisplayName.Trim());
        AppendCanonical(builder, lookupCodeNormalized);
        AppendCanonical(builder, item.ItemNumber?.Trim() ?? string.Empty);
        AppendCanonical(builder, item.Barcode?.Trim() ?? string.Empty);
        AppendCanonical(builder, item.RetailPrice.ToString("0.#############################", CultureInfo.InvariantCulture));
        AppendCanonical(builder, ((int)item.PriceSource).ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, item.PriceSourceLabel.Trim());
        AppendCanonical(builder, item.QuantityFactor.ToString("0.#############################", CultureInfo.InvariantCulture));
        AppendCanonical(builder, item.ProductImage ?? string.Empty);
        AppendCanonical(builder, FormatNullableDecimal(item.DiscountRate));
        AppendCanonical(builder, item.IsSpecialProduct ? "1" : "0");

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hashBytes);
    }

    private static void AppendCanonical(StringBuilder builder, string value)
    {
        builder
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('|');
    }

    private static string NormalizeStoreCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeProductCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeLookupCode(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
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

    private static int ReadInt32(SqliteDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            string stringValue => int.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture)
        };
    }

    private static int? ReadNullableInt32(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            string stringValue when string.IsNullOrWhiteSpace(stringValue) => null,
            string stringValue => int.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture)
        };
    }

    private static bool ReadBool(SqliteDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value switch
        {
            bool boolValue => boolValue,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            string stringValue when int.TryParse(stringValue, CultureInfo.InvariantCulture, out var parsed) => parsed != 0,
            string stringValue => bool.Parse(stringValue),
            _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
        };
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

        var value = reader.GetValue(ordinal);
        return value switch
        {
            decimal decimalValue => decimalValue,
            double doubleValue => Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture),
            long longValue => longValue,
            int intValue => intValue,
            string stringValue when string.IsNullOrWhiteSpace(stringValue) => null,
            string stringValue => decimal.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };
    }

    private static string FormatNullableDecimal(decimal? value)
    {
        return value?.ToString("0.#############################", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static int PreferredSpecialLookupRank(SellableItemDto item)
    {
        if (!string.IsNullOrWhiteSpace(item.Barcode) &&
            string.Equals(NormalizeLookupCode(item.LookupCode), NormalizeLookupCode(item.Barcode), StringComparison.Ordinal))
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(item.ItemNumber) &&
            string.Equals(NormalizeLookupCode(item.LookupCode), NormalizeLookupCode(item.ItemNumber), StringComparison.Ordinal))
        {
            return 1;
        }

        return 2;
    }
}
