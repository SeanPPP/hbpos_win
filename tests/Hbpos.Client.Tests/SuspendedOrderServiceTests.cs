using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class SuspendedOrderServiceTests
{
    [Fact]
    public async Task SuspendCurrentOrderAsync_saves_snapshot_clears_cart_and_keeps_local_orders_and_sync_queue_empty()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var cart = new PosCartService();
            var repository = new SuspendedOrderRepository(store);
            var service = new SuspendedOrderService(repository, cart);
            var session = CreateSession();
            var line = cart.AddItem(CreateItem(
                productCode: "SKU-HOLD-01",
                lookupCode: "hold-01",
                itemNumber: "ITEM-HOLD-01",
                price: 13.5m,
                priceSource: PriceSourceKind.StoreClearancePrice,
                productImage: "https://images.example/hold-01.jpg"));
            Assert.True(cart.SetLineQuantity(line, 2m));
            Assert.True(cart.SetLineDiscountPercent(line, 10m));

            await schema.InitializeAsync();

            var suspended = await service.SuspendCurrentOrderAsync(session);
            var pending = await service.GetPendingOrdersAsync(session.StoreCode);

            Assert.True(cart.IsEmpty);
            var summary = Assert.Single(pending);
            Assert.Equal(suspended.SuspendedOrderGuid, summary.SuspendedOrderGuid);
            Assert.Equal(27m, summary.TotalAmount);
            Assert.Equal(2.70m, summary.DiscountAmount);
            Assert.Equal(24.30m, summary.ActualAmount);

            await using var connection = await store.OpenConnectionAsync();
            Assert.Equal(0, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM LocalOrders;"));
            Assert.Equal(0, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM SyncQueue;"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task RecallOrderAsync_rejects_non_empty_cart()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var cart = new PosCartService();
            var repository = new SuspendedOrderRepository(store);
            var service = new SuspendedOrderService(repository, cart);

            await schema.InitializeAsync();
            cart.AddItem(CreateItem(productCode: "SKU-HOLD-02", lookupCode: "hold-02", price: 8m));
            var suspended = await service.SuspendCurrentOrderAsync(CreateSession());
            cart.AddItem(CreateItem(productCode: "SKU-LIVE-01", lookupCode: "live-01", price: 5m));

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecallOrderAsync(suspended.SuspendedOrderGuid));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task RecallOrderAsync_restores_snapshot_marks_recalled_and_hides_order_from_pending_list()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var cart = new PosCartService();
            var repository = new SuspendedOrderRepository(store);
            var service = new SuspendedOrderService(repository, cart);
            var session = CreateSession();
            var line = cart.AddItem(CreateItem(
                productCode: "SKU-HOLD-03",
                lookupCode: " hold-03 ",
                itemNumber: "ITEM-HOLD-03",
                price: 15m,
                priceSource: PriceSourceKind.StoreMultiCodeProduct,
                productImage: "https://images.example/hold-03.jpg"));
            Assert.True(cart.SetLineQuantity(line, 3m));
            Assert.True(cart.SetLineDiscountPercent(line, 12.5m));

            await schema.InitializeAsync();

            var suspended = await service.SuspendCurrentOrderAsync(session);
            var recalled = await service.RecallOrderAsync(suspended.SuspendedOrderGuid);
            var pending = await service.GetPendingOrdersAsync(session.StoreCode);
            var saved = await repository.GetAsync(suspended.SuspendedOrderGuid);

            Assert.Equal(suspended.SuspendedOrderGuid, recalled.SuspendedOrderGuid);
            line = Assert.Single(cart.Lines);
            Assert.Equal(3m, line.Quantity);
            Assert.Equal(15m, line.UnitPrice);
            Assert.Equal(5.63m, line.DiscountAmount);
            Assert.Equal("ITEM-HOLD-03", line.ItemNumber);
            Assert.Equal(" hold-03 ", line.LookupCode);
            Assert.Equal("https://images.example/hold-03.jpg", line.ProductImage);
            Assert.Equal(PriceSourceKind.StoreMultiCodeProduct, line.PriceSource);

            Assert.True(cart.SetLineQuantity(line, 4m));
            Assert.Equal(7.50m, line.DiscountAmount);

            Assert.Empty(pending);
            Assert.NotNull(saved);
            Assert.Equal(SuspendedOrderStatus.Recalled, saved.Status);

            await using var connection = await store.OpenConnectionAsync();
            Assert.Equal(0, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM LocalOrders;"));
            Assert.Equal(0, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM SyncQueue;"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
    }

    private static SellableItemDto CreateItem(
        string storeCode = "S001",
        string productCode = "SKU-001",
        string lookupCode = "690001",
        string displayName = "Milk 1L",
        string? itemNumber = null,
        decimal price = 10m,
        PriceSourceKind priceSource = PriceSourceKind.StoreRetailPrice,
        string? productImage = null,
        decimal quantityFactor = 1m)
    {
        return new SellableItemDto(
            StoreCode: storeCode,
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: displayName,
            LookupCode: lookupCode,
            ItemNumber: itemNumber ?? productCode,
            Barcode: lookupCode.Trim(),
            RetailPrice: price,
            PriceSource: priceSource,
            PriceSourceLabel: priceSource.ToString(),
            QuantityFactor: quantityFactor,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: productImage);
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-suspended-{Guid.NewGuid():N}.db");
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<int> ReadScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
