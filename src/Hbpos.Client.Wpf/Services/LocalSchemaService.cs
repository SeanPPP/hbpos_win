using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public interface ILocalSchemaService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalSchemaService(LocalSqliteStore store) : ILocalSchemaService
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        foreach (var sql in TableStatements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureLocalSellableItemIndexColumnsAsync(connection, cancellationToken);
        await EnsureDeviceCacheColumnsAsync(connection, cancellationToken);
        await EnsureLocalOrderLineColumnsAsync(connection, cancellationToken);
        await EnsureSuspendedOrderLineColumnsAsync(connection, cancellationToken);

        foreach (var sql in IndexStatements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureDeviceCacheColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "DeviceCache", cancellationToken);
        if (!columns.Contains("HardwareId"))
        {
            await ExecuteAsync(connection, "ALTER TABLE DeviceCache ADD COLUMN HardwareId TEXT NOT NULL DEFAULT '';", cancellationToken);
        }

        if (!columns.Contains("DeviceStatus"))
        {
            await ExecuteAsync(connection, "ALTER TABLE DeviceCache ADD COLUMN DeviceStatus INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        if (!columns.Contains("Message"))
        {
            await ExecuteAsync(connection, "ALTER TABLE DeviceCache ADD COLUMN Message TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("AuthorizationCodeProtected"))
        {
            await ExecuteAsync(connection, "ALTER TABLE DeviceCache ADD COLUMN AuthorizationCodeProtected TEXT NULL;", cancellationToken);
        }
    }

    private static async Task EnsureLocalSellableItemIndexColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "LocalSellableItemIndex", cancellationToken);
        if (!columns.Contains("LookupCodeNormalized"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalSellableItemIndex ADD COLUMN LookupCodeNormalized TEXT;", cancellationToken);
        }

        if (!columns.Contains("ContentHash"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalSellableItemIndex ADD COLUMN ContentHash TEXT;", cancellationToken);
        }

        if (!columns.Contains("SyncedAt"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalSellableItemIndex ADD COLUMN SyncedAt TEXT;", cancellationToken);
        }

        if (!columns.Contains("ProductImage"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalSellableItemIndex ADD COLUMN ProductImage TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("DiscountRate"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalSellableItemIndex ADD COLUMN DiscountRate TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("IsSpecialProduct"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalSellableItemIndex ADD COLUMN IsSpecialProduct INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        await ExecuteAsync(
            connection,
            """
            UPDATE LocalSellableItemIndex
            SET LookupCodeNormalized = UPPER(TRIM(LookupCode))
            WHERE LookupCodeNormalized IS NULL OR TRIM(LookupCodeNormalized) = '';
            """,
            cancellationToken);

        await ExecuteAsync(
            connection,
            """
            UPDATE LocalSellableItemIndex
            SET ContentHash =
                StoreCode || '|' ||
                ProductCode || '|' ||
                IFNULL(ReferenceCode, '') || '|' ||
                DisplayName || '|' ||
                LookupCode || '|' ||
                IFNULL(ItemNumber, '') || '|' ||
                IFNULL(Barcode, '') || '|' ||
                RetailPrice || '|' ||
                PriceSource || '|' ||
                PriceSourceLabel || '|' ||
                QuantityFactor || '|' ||
                IFNULL(ProductImage, '') || '|' ||
                IFNULL(DiscountRate, '') || '|' ||
                IsSpecialProduct
            WHERE ContentHash IS NULL OR TRIM(ContentHash) = '';
            """,
            cancellationToken);

        await ExecuteAsync(
            connection,
            """
            UPDATE LocalSellableItemIndex
            SET SyncedAt = COALESCE(UpdatedAt, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            WHERE SyncedAt IS NULL OR TRIM(SyncedAt) = '';
            """,
            cancellationToken);
    }

    private static async Task EnsureLocalOrderLineColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "LocalOrderLines", cancellationToken);
        if (!columns.Contains("ItemNumber"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalOrderLines ADD COLUMN ItemNumber TEXT NULL;", cancellationToken);
        }
    }

    private static async Task EnsureSuspendedOrderLineColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "SuspendedOrderLines", cancellationToken);
        if (!columns.Contains("DiscountPercent"))
        {
            await ExecuteAsync(connection, "ALTER TABLE SuspendedOrderLines ADD COLUMN DiscountPercent TEXT NULL;", cancellationToken);
        }
    }

    private static async Task<HashSet<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static readonly string[] TableStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS DeviceCache (
            DeviceCode TEXT PRIMARY KEY,
            StoreCode TEXT NOT NULL,
            StoreName TEXT NOT NULL,
            HardwareId TEXT NOT NULL DEFAULT '',
            DeviceStatus INTEGER NOT NULL DEFAULT 0,
            IsAllowed INTEGER NOT NULL,
            Message TEXT NULL,
            AuthorizationCodeProtected TEXT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS CashierCache (
            CashierId TEXT PRIMARY KEY,
            CashierName TEXT NOT NULL,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            RolesJson TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalSellableItemIndex (
            StoreCode TEXT NOT NULL,
            ProductCode TEXT NOT NULL,
            ReferenceCode TEXT NULL,
            DisplayName TEXT NOT NULL,
            LookupCode TEXT NOT NULL,
            LookupCodeNormalized TEXT NOT NULL,
            ItemNumber TEXT NULL,
            Barcode TEXT NULL,
            ProductImage TEXT NULL,
            DiscountRate TEXT NULL,
            IsSpecialProduct INTEGER NOT NULL DEFAULT 0,
            RetailPrice TEXT NOT NULL,
            PriceSource INTEGER NOT NULL,
            PriceSourceLabel TEXT NOT NULL,
            QuantityFactor TEXT NOT NULL,
            UpdatedAt TEXT NULL,
            ContentHash TEXT NOT NULL,
            SyncedAt TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalOrders (
            OrderGuid TEXT PRIMARY KEY,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            CashierId TEXT NOT NULL,
            CashierName TEXT NOT NULL,
            SoldAt TEXT NOT NULL,
            TotalAmount TEXT NOT NULL,
            DiscountAmount TEXT NOT NULL,
            ActualAmount TEXT NOT NULL,
            SyncStatus TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalOrderLines (
            OrderLineGuid TEXT PRIMARY KEY,
            OrderGuid TEXT NOT NULL,
            ProductCode TEXT NOT NULL,
            ReferenceCode TEXT NULL,
            DisplayName TEXT NOT NULL,
            LookupCode TEXT NOT NULL,
            ItemNumber TEXT NULL,
            Quantity TEXT NOT NULL,
            UnitPrice TEXT NOT NULL,
            DiscountAmount TEXT NOT NULL,
            ActualAmount TEXT NOT NULL,
            PriceSource INTEGER NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalPayments (
            PaymentGuid TEXT PRIMARY KEY,
            OrderGuid TEXT NOT NULL,
            Method INTEGER NOT NULL,
            Amount TEXT NOT NULL,
            Reference TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS SyncQueue (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            EntityId TEXT NOT NULL,
            EntityType TEXT NOT NULL,
            Status TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            LastTriedAt TEXT NULL,
            ErrorMessage TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalShifts (
            ShiftGuid TEXT PRIMARY KEY,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            CashierId TEXT NOT NULL,
            OpenedAt TEXT NOT NULL,
            ClosedAt TEXT NULL,
            OpeningCash TEXT NOT NULL,
            ClosingCash TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS AppSettings (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalSpecialProductSortOrder (
            StoreCode TEXT NOT NULL,
            ProductCode TEXT NOT NULL,
            SortOrder INTEGER NOT NULL,
            UpdatedAt TEXT NOT NULL,
            PRIMARY KEY (StoreCode, ProductCode)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS SuspendedOrders (
            SuspendedOrderGuid TEXT PRIMARY KEY,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            CashierId TEXT NOT NULL,
            CashierName TEXT NOT NULL,
            SuspendedAt TEXT NOT NULL,
            TotalAmount TEXT NOT NULL,
            DiscountAmount TEXT NOT NULL,
            ActualAmount TEXT NOT NULL,
            Status INTEGER NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS SuspendedOrderLines (
            SuspendedOrderLineGuid TEXT PRIMARY KEY,
            SuspendedOrderGuid TEXT NOT NULL,
            StoreCode TEXT NOT NULL,
            ProductCode TEXT NOT NULL,
            ReferenceCode TEXT NULL,
            DisplayName TEXT NOT NULL,
            LookupCode TEXT NOT NULL,
            ItemNumber TEXT NULL,
            ProductImage TEXT NULL,
            Quantity TEXT NOT NULL,
            UnitPrice TEXT NOT NULL,
            DiscountAmount TEXT NOT NULL,
            DiscountPercent TEXT NULL,
            ActualAmount TEXT NOT NULL,
            PriceSource INTEGER NOT NULL,
            PriceSourceLabel TEXT NOT NULL
        );
        """
    ];

    private static readonly string[] IndexStatements =
    [
        """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LocalSellableItemIndex_Store_LookupCodeNormalized
        ON LocalSellableItemIndex (StoreCode, LookupCodeNormalized);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalSellableItemIndex_Lookup
        ON LocalSellableItemIndex (StoreCode, LookupCode, Barcode, ItemNumber);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalSellableItemIndex_Store_Special_Product
        ON LocalSellableItemIndex (StoreCode, IsSpecialProduct, ProductCode);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalOrderLines_OrderGuid_ItemNumber_LookupCode
        ON LocalOrderLines (OrderGuid, ItemNumber, LookupCode);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_SuspendedOrders_Store_Status_SuspendedAt
        ON SuspendedOrders (StoreCode, Status, SuspendedAt);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_SuspendedOrderLines_Order_ItemNumber_LookupCode
        ON SuspendedOrderLines (SuspendedOrderGuid, ItemNumber, LookupCode);
        """
    ];
}
