using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class CashPaymentWorkflowServiceTests
{
    [Fact]
    public void Cash_payment_workflow_parses_tendered_amount_and_calculates_change()
    {
        var workflow = CreateWorkflow();

        var parsed = workflow.TryParseTenderedAmount("10.5", out var tenderedAmount);
        var change = workflow.CalculateChange("10.5", 7.81m);

        Assert.True(parsed);
        Assert.Equal(10.5m, tenderedAmount);
        Assert.Equal(2.69m, change);
    }

    [Fact]
    public void Cash_payment_workflow_rejects_invalid_tendered_amount()
    {
        var workflow = CreateWorkflow();

        var parsed = workflow.TryParseTenderedAmount("cash", out var tenderedAmount);
        var change = workflow.CalculateChange("cash", 7.81m);

        Assert.False(parsed);
        Assert.Equal(0m, tenderedAmount);
        Assert.Equal(0m, change);
    }

    [Fact]
    public async Task Cash_payment_workflow_persists_order_clears_cart_and_refreshes_pending_sync()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-301", "Workflow Tea", "930301", 4.4m));
        var orders = new RecordingOrderRepository();
        var syncQueue = new StubSyncQueueRepository(pendingCount: 3);
        var workflow = new CashPaymentWorkflowService(new CashCheckoutService(), orders, syncQueue);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.CompleteAsync(cart, session, "5");

        var savedOrder = Assert.Single(orders.SavedOrders);
        Assert.Same(savedOrder, result.Order);
        Assert.Equal(4.4m, savedOrder.ActualAmount);
        Assert.Equal(5m, result.TenderedAmount);
        Assert.Equal(0.6m, result.ChangeAmount);
        Assert.Empty(cart.Lines);
        Assert.Equal(3, result.PendingSyncCount);
        Assert.Equal(3, result.UpdatedSession.PendingSyncCount);
        Assert.Equal(savedOrder.OrderGuid, result.Order.OrderGuid);
    }

    private static ICashPaymentWorkflowService CreateWorkflow()
    {
        return new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1));
    }

    private static SellableItemDto CreateItem(string productCode, string name, string lookupCode, decimal price)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: name,
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private sealed class RecordingOrderRepository : ILocalOrderRepository
    {
        public List<LocalOrder> SavedOrders { get; } = [];

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            SavedOrders.Add(order);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return GetRecentOrdersAsync(take, cancellationToken);
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalOrder?>(SavedOrders.LastOrDefault(order => order.OrderGuid == orderGuid));
        }
    }

    private sealed class StubSyncQueueRepository(int pendingCount) : ISyncQueueRepository
    {
        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(pendingCount);
        }

        public Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SyncQueueOverview(pendingCount, 0, 0, null));
        }

        public Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SyncQueueListItem>>([]);
        }
    }
}
