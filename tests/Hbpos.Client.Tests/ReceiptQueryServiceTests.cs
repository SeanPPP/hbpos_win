using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class ReceiptQueryServiceTests
{
    [Fact]
    public async Task Receipt_query_service_maps_receipt_preview_from_order()
    {
        var order = CreateOrder(
            Guid.NewGuid(),
            soldAt: new DateTimeOffset(2026, 5, 24, 9, 30, 0, TimeSpan.Zero),
            actualAmount: 8.4m);
        var service = new ReceiptQueryService(new StubOrderRepository([order]));

        var receipt = await service.GetReceiptAsync(order.OrderGuid);

        Assert.NotNull(receipt);
        Assert.Equal(order.OrderGuid, receipt.OrderGuid);
        Assert.Equal(order.StoreCode, receipt.StoreCode);
        Assert.Equal(order.DeviceCode, receipt.DeviceCode);
        Assert.Equal(order.CashierName, receipt.CashierName);
        Assert.Equal(order.TotalAmount, receipt.TotalAmount);
        Assert.Equal(order.DiscountAmount, receipt.DiscountAmount);
        Assert.Equal(order.ActualAmount, receipt.ActualAmount);
        Assert.Equal("#" + order.OrderGuid.ToString("N")[..10].ToUpperInvariant(), receipt.TransactionIdDisplay);
        Assert.Equal(order.SoldAt.ToLocalTime().ToString("MMM dd, yyyy HH:mm"), receipt.SoldAtDisplay);
        Assert.Equal(2, receipt.Lines.Count);
        Assert.Equal(PaymentMethodKind.Cash, Assert.Single(receipt.Payments).Method);
    }

    [Fact]
    public async Task Receipt_query_service_returns_latest_receipt()
    {
        var olderOrder = CreateOrder(
            Guid.NewGuid(),
            soldAt: new DateTimeOffset(2026, 5, 24, 8, 0, 0, TimeSpan.Zero),
            actualAmount: 5m);
        var latestOrder = CreateOrder(
            Guid.NewGuid(),
            soldAt: new DateTimeOffset(2026, 5, 24, 10, 0, 0, TimeSpan.Zero),
            actualAmount: 9m);
        var service = new ReceiptQueryService(new StubOrderRepository([olderOrder, latestOrder]));

        var receipt = await service.GetLatestReceiptAsync();

        Assert.NotNull(receipt);
        Assert.Equal(latestOrder.OrderGuid, receipt.OrderGuid);
        Assert.Equal(latestOrder.ActualAmount, receipt.ActualAmount);
    }

    private static LocalOrder CreateOrder(Guid orderGuid, DateTimeOffset soldAt, decimal actualAmount)
    {
        var totalAmount = actualAmount + 0.2m;
        var discountAmount = totalAmount - actualAmount;
        var firstLineActual = decimal.Round(actualAmount - 4m, 2, MidpointRounding.AwayFromZero);
        var secondLineActual = decimal.Round(actualAmount - firstLineActual, 2, MidpointRounding.AwayFromZero);

        return new LocalOrder(
            orderGuid,
            "S001",
            "POS-01",
            "C001",
            "Alice",
            soldAt,
            totalAmount,
            discountAmount,
            actualAmount,
            [
                new LocalOrderLine(Guid.NewGuid(), "SKU-401", null, "Receipt Noodles", "930401", "ITEM-401", 1m, firstLineActual, 0m, firstLineActual, PriceSourceKind.StoreRetailPrice),
                new LocalOrderLine(Guid.NewGuid(), "SKU-402", null, "Receipt Juice", "930402", "ITEM-402", 1m, 4.2m, discountAmount, secondLineActual, PriceSourceKind.ProductBase)
            ],
            [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, actualAmount, null)]);
    }

    private sealed class StubOrderRepository(IEnumerable<LocalOrder> orders) : ILocalOrderRepository
    {
        private readonly Dictionary<Guid, LocalOrder> _orders = orders.ToDictionary(order => order.OrderGuid);
        private readonly IReadOnlyList<LocalOrderSummary> _summaries = orders
            .OrderByDescending(order => order.SoldAt)
            .Select(order => new LocalOrderSummary(
                order.OrderGuid,
                order.StoreCode,
                order.DeviceCode,
                order.CashierName,
                order.SoldAt,
                order.TotalAmount,
                order.DiscountAmount,
                order.ActualAmount,
                "Pending",
                order.Lines.Count,
                "Cash"))
            .ToList();

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            _orders[order.OrderGuid] = order;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>(_summaries.Take(take).ToList());
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
            return Task.FromResult(_orders.TryGetValue(orderGuid, out var order) ? order : null);
        }
    }
}
