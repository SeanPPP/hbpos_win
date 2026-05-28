using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;
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
    public async Task GetPendingOrdersAsync_filters_by_device_when_terminal_is_selected()
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

            await schema.InitializeAsync();

            cart.AddItem(CreateItem(productCode: "SKU-POS-01", lookupCode: "pos-01", price: 6m));
            var pos01Order = await service.SuspendCurrentOrderAsync(session);

            cart.AddItem(CreateItem(productCode: "SKU-POS-02", lookupCode: "pos-02", price: 9m));
            var pos02Order = await service.SuspendCurrentOrderAsync(session with { DeviceCode = "POS-02" });

            var allTerminals = await service.GetPendingOrdersAsync(session.StoreCode, deviceCode: null);
            var pos01Only = await service.GetPendingOrdersAsync(session.StoreCode, deviceCode: "POS-01");
            var pos02Only = await service.GetPendingOrdersAsync(session.StoreCode, deviceCode: "POS-02");

            Assert.Equal(2, allTerminals.Count);
            Assert.Contains(allTerminals, order => order.SuspendedOrderGuid == pos01Order.SuspendedOrderGuid);
            Assert.Contains(allTerminals, order => order.SuspendedOrderGuid == pos02Order.SuspendedOrderGuid);
            Assert.Equal(pos01Order.SuspendedOrderGuid, Assert.Single(pos01Only).SuspendedOrderGuid);
            Assert.Equal(pos02Order.SuspendedOrderGuid, Assert.Single(pos02Only).SuspendedOrderGuid);
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

    [Fact]
    public async Task RecallOrderAsync_restores_return_line_context_and_card_refund_capacity()
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
            var originalOrderGuid = Guid.NewGuid();
            var originalLineGuid = Guid.NewGuid();
            const string returnSourceKey = "S001:ORIGINAL-ORDER-01:LINE-01";
            const string returnReason = "Damaged packaging";

            await schema.InitializeAsync();
            cart.AddReturnLine(new ReturnCartLineRequest(
                "S001",
                "SKU-RETURN-01",
                "REF-RETURN-01",
                "Returned item",
                "return-01",
                "ITEM-RETURN-01",
                "https://images.example/return-01.jpg",
                1m,
                12m,
                PriceSourceKind.StoreRetailPrice,
                PriceSourceKind.StoreRetailPrice.ToString(),
                returnSourceKey,
                originalOrderGuid,
                originalLineGuid,
                ReturnReason: returnReason));
            cart.AddReturnPaymentCapacities(
            [
                new OrderReturnPaymentCapacityDto(
                    PaymentMethodKind.Card,
                    OriginalAmount: 12m,
                    RefundedAmount: 3m,
                    RemainingAmount: 9m,
                    Reference: "SQ:original-card-payment",
                    OriginalOrderGuid: originalOrderGuid)
            ]);

            var suspended = await service.SuspendCurrentOrderAsync(session);
            await service.RecallOrderAsync(suspended.SuspendedOrderGuid);

            var recalledLine = Assert.Single(cart.Lines);
            Assert.Equal(CartLineKind.Return, recalledLine.Kind);
            Assert.True(recalledLine.IsReturnLine);
            Assert.Equal(returnSourceKey, recalledLine.ReturnSourceKey);
            Assert.Equal(originalOrderGuid, recalledLine.OriginalOrderGuid);
            Assert.Equal(originalLineGuid, recalledLine.OriginalOrderLineGuid);
            Assert.Equal(returnReason, recalledLine.ReturnReason);
            Assert.Equal(-12m, cart.ActualAmount);

            var cardCapacity = Assert.Single(cart.ReturnPaymentCapacities);
            Assert.Equal(PaymentMethodKind.Card, cardCapacity.Method);
            Assert.Equal(12m, cardCapacity.OriginalAmount);
            Assert.Equal(3m, cardCapacity.RefundedAmount);
            Assert.Equal(9m, cardCapacity.RemainingAmount);
            Assert.Equal("SQ:original-card-payment", cardCapacity.Reference);
            Assert.Equal(originalOrderGuid, cardCapacity.OriginalOrderGuid);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public void Return_cart_snapshot_exposes_return_reason_context()
    {
        Assert.Contains(
            typeof(ReturnCartLineRequest).GetConstructors().Single().GetParameters(),
            parameter => string.Equals(parameter.Name, "ReturnReason", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(typeof(CartLine).GetProperty("ReturnReason"));
        Assert.NotNull(typeof(PosCartLineSnapshot).GetProperty("ReturnReason"));
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
