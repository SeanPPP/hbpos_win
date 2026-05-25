using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Api.Tests;

public sealed class OrderSyncServiceTests
{
    [Fact]
    public async Task SyncAsync_ReturnsAlreadySyncedWhenOrderExists()
    {
        var orderGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: true);
        var service = new OrderSyncService(repository, new OrderSyncPlanner());

        var response = await service.SyncAsync(CreateRequest(orderGuid), CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.True(response.AlreadySynced);
        Assert.Equal("AlreadySynced", response.Message);
        Assert.False(repository.InsertCalled);
    }

    [Fact]
    public async Task SyncAsync_InsertsSnapshotWhenOrderDoesNotExist()
    {
        var orderGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false);
        var service = new OrderSyncService(repository, new OrderSyncPlanner());

        var response = await service.SyncAsync(CreateRequest(orderGuid), CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.False(response.AlreadySynced);
        Assert.True(repository.InsertCalled);
        Assert.Equal(orderGuid.ToString("D"), repository.LastPlan?.Order.OrderGuid);
        Assert.Equal(9.99m, repository.LastPlan?.Lines.Single().Price);
        Assert.Equal("SOURCE-GUID-01", repository.LastPlan?.Lines.Single().ReferenceGUID);
        Assert.Equal("priceSource=1", repository.LastPlan?.Lines.Single().Remark);
    }

    [Fact]
    public void Planner_WritesItemNumberAsItemNoMetadata()
    {
        var request = CreateRequest(Guid.NewGuid(), itemNumber: "ITEM-1001");

        var plan = new OrderSyncPlanner().CreatePlan(request);

        var line = Assert.Single(plan.Lines);
        Assert.Equal("P01", line.ProductCode);
        Assert.Contains("itemNo=ITEM-1001", line.Remark);
    }

    private static OrderSyncRequest CreateRequest(Guid orderGuid, string? itemNumber = null)
    {
        return new OrderSyncRequest(
            orderGuid,
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            9.99m,
            0m,
            9.99m,
            [
                new OrderLineSyncDto(
                    Guid.NewGuid(),
                    "P01",
                    "SOURCE-GUID-01",
                    "Apple",
                    "BAR01",
                    1m,
                    9.99m,
                    0m,
                    9.99m,
                    PriceSourceKind.StoreRetailPrice,
                    itemNumber)
            ],
            [
                new PaymentSyncDto(
                    Guid.NewGuid(),
                    PaymentMethodKind.Cash,
                    9.99m,
                    null)
            ]);
    }

    private sealed class FakeOrderRepository(bool exists) : IOrderRepository
    {
        public bool InsertCalled { get; private set; }

        public OrderSyncPlan? LastPlan { get; private set; }

        public Task<bool> ExistsAsync(Guid orderGuid, CancellationToken cancellationToken)
        {
            return Task.FromResult(exists);
        }

        public Task InsertAsync(OrderSyncPlan plan, CancellationToken cancellationToken)
        {
            InsertCalled = true;
            LastPlan = plan;
            return Task.CompletedTask;
        }
    }
}
