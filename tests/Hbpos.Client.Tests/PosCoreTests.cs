using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class PosCoreTests
{
    [Fact]
    public void Local_price_index_prefers_exact_barcode_then_keyword_matches()
    {
        var index = new LocalSellableItemIndex();

        var barcodeItem = CreateItem("SKU-001", "Milk 1L", "690001", PriceSourceKind.StoreRetailPrice, 12.5m);
        var keywordItem = CreateItem("SKU-002", "Apple Juice", "690002", PriceSourceKind.ProductBase, 8.8m);
        index.ReplaceAll([keywordItem, barcodeItem]);

        var barcodeMatches = index.Search("690001");
        var keywordMatches = index.Search("juice");

        Assert.Equal("SKU-001", Assert.Single(barcodeMatches).ProductCode);
        Assert.Equal("SKU-002", Assert.Single(keywordMatches).ProductCode);
    }

    [Fact]
    public void Local_price_index_exact_lookup_matches_lookup_code_within_store()
    {
        var index = new LocalSellableItemIndex();
        var barcodeItem = CreateItem("SKU-001", "Milk 1L", "690001", PriceSourceKind.StoreRetailPrice, 12.5m, itemNumber: "ITEM-001");
        var productCodeItem = CreateItem("SKU-002", "Apple Juice", "690002", PriceSourceKind.ProductBase, 8.8m, itemNumber: "ITEM-002");
        var otherStoreItem = CreateItem("SKU-003", "Other Store Milk", "690001", PriceSourceKind.ProductBase, 9.9m, storeCode: "S002");
        index.ReplaceAll([barcodeItem, productCodeItem, otherStoreItem]);

        Assert.Equal("SKU-001", Assert.Single(index.FindExactMatches("S001", "690001")).ProductCode);
        Assert.Equal("SKU-003", Assert.Single(index.FindExactMatches("S002", "690001")).ProductCode);
        Assert.Empty(index.FindExactMatches("S001", "ITEM-001"));
        Assert.Empty(index.FindExactMatches("S001", "sku-002"));
    }

    [Fact]
    public void Local_price_index_search_can_filter_by_store()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-001", "Shared Milk", "690001", PriceSourceKind.StoreRetailPrice, 12.5m, storeCode: "S001"),
            CreateItem("SKU-002", "Shared Milk", "690001", PriceSourceKind.StoreRetailPrice, 9.9m, storeCode: "S002")
        ]);

        var matches = index.Search("S002", "milk");

        var item = Assert.Single(matches);
        Assert.Equal("S002", item.StoreCode);
        Assert.Equal("SKU-002", item.ProductCode);
    }

    [Fact]
    public void Local_price_index_exact_lookup_deduplicates_same_item_code_aliases()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("690001", "Milk 1L", "690001", PriceSourceKind.StoreRetailPrice, 12.5m, itemNumber: "690001")]);

        var matches = index.FindExactMatches("S001", "690001");

        Assert.Single(matches);
    }

    [Fact]
    public void Cart_adds_scanned_item_and_calculates_totals()
    {
        var cart = new PosCartService();
        var item = CreateItem("SKU-001", "Milk 1L", "690001", PriceSourceKind.StoreRetailPrice, 12.5m);

        cart.AddItem(item);
        cart.AddItem(item);

        Assert.Equal(2, cart.Lines[0].Quantity);
        Assert.Equal(25m, cart.TotalAmount);
        Assert.Equal(25m, cart.ActualAmount);
    }

    [Fact]
    public void Cash_checkout_creates_order_snapshot_and_change()
    {
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var item = CreateItem("SKU-001", "Milk 1L", "690001", PriceSourceKind.StoreClearancePrice, 9.9m);

        cart.AddItem(item);
        var result = checkout.CreateCashOrder(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            tenderedAmount: 10m);

        Assert.Equal(9.9m, result.Order.ActualAmount);
        Assert.Equal(0.1m, result.ChangeAmount);
        Assert.Equal("Milk 1L", Assert.Single(result.Order.Lines).DisplayName);
        Assert.Equal(PriceSourceKind.StoreClearancePrice, result.Order.Lines[0].PriceSource);
        Assert.Equal(9.9m, Assert.Single(result.Order.Payments).Amount);
    }

    [Fact]
    public void Cash_checkout_rejects_zero_price_cart_lines()
    {
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        cart.AddItem(CreateItem("SKU-ZERO", "Zero Tea", "690099", PriceSourceKind.StoreRetailPrice, 0m));

        Assert.Throws<InvalidOperationException>(() => checkout.CreateCashOrder(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            tenderedAmount: 0m));
    }

    [Fact]
    public void Cash_checkout_rejects_non_integer_cart_quantities()
    {
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var line = cart.AddItem(CreateItem("SKU-FRACTION", "Fraction Tea", "690098", PriceSourceKind.StoreRetailPrice, 4m));
        SetUnsafeQuantity(line, 1.5m);

        Assert.Throws<InvalidOperationException>(() => checkout.CreateCashOrder(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            tenderedAmount: 10m));
    }

    [Fact]
    public async Task Pos_terminal_sync_refreshes_online_state_before_syncing()
    {
        var index = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var item = CreateItem("SKU-001", "Milk 1L", "690001", PriceSourceKind.StoreRetailPrice, 12.5m);
        var refreshedOnline = false;
        var synced = false;
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", false, 0),
            onOpenPayment: null,
            refreshOnlineAsync: _ =>
            {
                refreshedOnline = true;
                return Task.FromResult(true);
            },
            syncCatalogAsync: _ =>
            {
                synced = true;
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([item]);
            });

        await viewModel.SyncCommand.ExecuteAsync(null);

        Assert.True(refreshedOnline);
        Assert.True(synced);
        Assert.True(viewModel.Session.IsOnline);
        Assert.Equal("Catalog sync completed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Local_repositories_initialize_catalog_order_and_sync_queue()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-client-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var catalog = new LocalCatalogRepository(store);
            var orders = new LocalOrderRepository(store);
            var syncQueue = new SyncQueueRepository(store);
            var item = CreateItem("SKU-003", "Orange", "690003", PriceSourceKind.ProductBase, 3.2m);
            var cart = new PosCartService();
            var checkout = new CashCheckoutService();

            await schema.InitializeAsync();
            await catalog.ReplaceSellableItemsAsync([item]);

            var cachedItems = await catalog.LoadSellableItemsAsync();
            cart.AddItem(Assert.Single(cachedItems));
            var result = checkout.CreateCashOrder(
                cart,
                new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
                tenderedAmount: 5m);

            await orders.SavePendingOrderAsync(result.Order);

            Assert.Equal("SKU-003", cachedItems[0].ProductCode);
            Assert.Equal(1, await syncQueue.CountPendingAsync());
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Local_order_repository_reads_recent_order_with_lines_and_payments()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-client-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var order = CreateLocalOrder();

            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);

            var recentOrders = await orders.GetRecentOrdersAsync();
            var savedOrder = await orders.GetOrderAsync(order.OrderGuid);

            var summary = Assert.Single(recentOrders);
            Assert.Equal(order.OrderGuid, summary.OrderGuid);
            Assert.Equal(2, summary.LineCount);
            Assert.Equal("Cash", summary.PaymentSummary);
            Assert.NotNull(savedOrder);
            Assert.Equal(2, savedOrder.Lines.Count);
            Assert.Equal(order.ActualAmount, Assert.Single(savedOrder.Payments).Amount);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Transaction_history_view_model_loads_at_least_one_order()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-client-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var order = CreateLocalOrder();

            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);

            var viewModel = new TransactionHistoryViewModel(orders);
            await viewModel.LoadAsync();

            Assert.NotEmpty(viewModel.Orders);
            Assert.Equal(order.OrderGuid, viewModel.SelectedOrder?.OrderGuid);
            Assert.Equal(order.ActualAmount, viewModel.PreviewTotal);
            Assert.Equal(2, viewModel.ReceiptLines.Count);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public void Payment_success_view_model_shows_receipt_total()
    {
        var order = CreateLocalOrder();
        var viewModel = new PaymentSuccessViewModel();

        viewModel.LoadFromOrder(order);

        Assert.Equal(order.ActualAmount, viewModel.TotalAmountPaid);
        Assert.Equal(order.Lines.Sum(line => line.ActualAmount), viewModel.Subtotal);
        Assert.Equal(2, viewModel.ReceiptLines.Count);
        Assert.Equal(order.OrderGuid, viewModel.TransactionId);
    }

    private static SellableItemDto CreateItem(
        string productCode,
        string name,
        string barcode,
        PriceSourceKind priceSource,
        decimal price,
        string storeCode = "S001",
        string? itemNumber = null)
    {
        return new SellableItemDto(
            StoreCode: storeCode,
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: name,
            LookupCode: barcode,
            ItemNumber: itemNumber ?? productCode,
            Barcode: barcode,
            RetailPrice: price,
            PriceSource: priceSource,
            PriceSourceLabel: priceSource.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static void SetUnsafeQuantity(CartLine line, decimal quantity)
    {
        var field = typeof(CartLine).GetField("_quantity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(line, quantity);
    }

    private static LocalOrder CreateLocalOrder()
    {
        var lines = new[]
        {
            new LocalOrderLine(Guid.NewGuid(), "SKU-101", null, "Organic Gala Apples", "690101", 2m, 2.50m, 0m, 5.00m, PriceSourceKind.StoreRetailPrice),
            new LocalOrderLine(Guid.NewGuid(), "SKU-102", null, "Whole Grain Bread", "690102", 1m, 4.20m, 0.20m, 4.00m, PriceSourceKind.ProductBase)
        };

        return new LocalOrder(
            Guid.NewGuid(),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.UtcNow,
            9.20m,
            0.20m,
            9.00m,
            lines,
            [new LocalPayment(Guid.NewGuid(), Hbpos.Contracts.Orders.PaymentMethodKind.Cash, 9.00m, null)]);
    }
}
