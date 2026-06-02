using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class ReceiptReturnsWorkflowServiceTests
{
    [Fact]
    public async Task LookupOrderAsync_UsesRemoteReturnContextAndCalculatesAvailableQuantity()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var remote = new FakeRemoteOrderHistoryService
        {
            QueryResult = new RemoteOrderHistoryResult(
            [
                new RemoteOrderHistorySummary(orderGuid, "S001", "POS-01", "Alice", DateTimeOffset.UtcNow, 20m, 0m, 20m, 1, "Cash", "Completed")
            ]),
            ReturnContext = new OrderReturnContextDto(
                CreateRemoteOrder(orderGuid, lineGuid, quantity: 2m, actualAmount: 20m),
                [
                    new OrderReturnRecordDto(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        orderGuid,
                        lineGuid,
                        "SKU-001",
                        "REF-001",
                        1m,
                        10m,
                        "C01",
                        DateTimeOffset.UtcNow)
                ])
        };
        var service = CreateService(remote);

        var result = await service.LookupOrderAsync(CreateOnlineSession(), orderGuid.ToString("D"));

        Assert.NotNull(result.Order);
        Assert.True(result.IsRemote);
        Assert.False(result.ReturnRecordsMayBeStale);
        var line = Assert.Single(result.Order.Lines);
        Assert.Equal(1m, line.ReturnedQuantity);
        Assert.Equal(1m, line.AvailableQuantity);
        Assert.Equal(10m, line.ReturnUnitAmount);
    }

    [Fact]
    public async Task LookupOrderAsync_FallsBackToLocalOrderWhenRemoteFails()
    {
        var order = CreateLocalOrder(Guid.NewGuid());
        var localRepository = new FakeLocalOrderRepository([order]);
        var service = CreateService(new ThrowingRemoteOrderHistoryService(), localRepository);

        var result = await service.LookupOrderAsync(CreateOnlineSession(), order.OrderGuid.ToString("D"));

        Assert.NotNull(result.Order);
        Assert.False(result.IsRemote);
        Assert.True(result.ReturnRecordsMayBeStale);
        Assert.Equal(order.OrderGuid, result.Order.OrderGuid);
    }

    [Fact]
    public async Task LookupOrderAsync_LocalCardPaymentCapacitiesCarryOriginalOrderGuid()
    {
        var orderGuid = Guid.NewGuid();
        var order = new LocalOrder(
            orderGuid,
            "S001",
            "POS-01",
            "C01",
            "Alice",
            DateTimeOffset.UtcNow,
            10m,
            0m,
            10m,
            [
                new LocalOrderLine(Guid.NewGuid(), "SKU-001", "REF-001", "Milk", "690001", "ITEM-001", 1m, 10m, 0m, 10m, PriceSourceKind.StoreRetailPrice)
            ],
            [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Card, 10m, "SQ:local-payment-1")]);
        var service = CreateService(localRepository: new FakeLocalOrderRepository([order]));

        var result = await service.LookupOrderAsync(CreateOnlineSession(), orderGuid.ToString("D"));

        Assert.NotNull(result.Order);
        var capacity = Assert.Single(result.Order.PaymentCapacities);
        Assert.Equal(PaymentMethodKind.Card, capacity.Method);
        Assert.Equal("SQ:local-payment-1", capacity.Reference);
        Assert.Equal(orderGuid, capacity.OriginalOrderGuid);
    }

    [Fact]
    public void LookupNoReceiptProduct_ReturnsCurrentLocalCatalogItem()
    {
        var priceIndex = new LocalSellableItemIndex();
        priceIndex.ReplaceAll([CreateItem()]);
        var service = CreateService(priceIndex: priceIndex);

        var result = service.LookupNoReceiptProduct(CreateOnlineSession(), "690001");

        Assert.NotNull(result.Item);
        Assert.Equal("SKU-001", result.Item.ProductCode);
    }

    [Fact]
    public void CreateNoReceiptOpenItem_UsesOpenItemWithEnteredNameAndPrice()
    {
        var priceIndex = new LocalSellableItemIndex();
        priceIndex.ReplaceAll([CreateItem("OPEN-SKU", "Open Item", "OPENITEM", 0m)]);
        var service = CreateService(priceIndex: priceIndex);

        var first = service.CreateNoReceiptOpenItem(CreateOnlineSession(), "Manual Refund", 12.34m);
        var second = service.CreateNoReceiptOpenItem(CreateOnlineSession(), "Manual Refund", 12.34m);

        Assert.NotNull(first.Line);
        Assert.NotNull(second.Line);
        Assert.Equal("OPEN-SKU", first.Line.ProductCode);
        Assert.Equal("Manual Refund", first.Line.DisplayName);
        Assert.Equal("OPENITEM", first.Line.LookupCode);
        Assert.Equal(12.34m, first.Line.UnitPrice);
        Assert.Null(first.Line.OriginalOrderGuid);
        Assert.Null(first.Line.OriginalOrderLineGuid);
        Assert.NotEqual(first.Line.ReturnSourceKey, second.Line.ReturnSourceKey);
    }

    [Fact]
    public void CreateNoReceiptOpenItem_ReturnsErrorWhenOpenItemIsMissingOrDuplicated()
    {
        var missingService = CreateService(priceIndex: new LocalSellableItemIndex());

        var missing = missingService.CreateNoReceiptOpenItem(CreateOnlineSession(), "Manual Refund", 12.34m);

        Assert.Null(missing.Line);
        Assert.Equal("OPENITEM was not found in the local catalog.", missing.StatusMessage);

        var duplicateIndex = new LocalSellableItemIndex();
        duplicateIndex.ReplaceAll([
            CreateItem("OPEN-SKU-1", "Open Item 1", "OPENITEM", 0m),
            CreateItem("OPEN-SKU-2", "Open Item 2", "OPENITEM", 0m)
        ]);
        var duplicateService = CreateService(priceIndex: duplicateIndex);

        var duplicate = duplicateService.CreateNoReceiptOpenItem(CreateOnlineSession(), "Manual Refund", 12.34m);

        Assert.Null(duplicate.Line);
        Assert.Equal("Multiple OPENITEM records were found in the local catalog.", duplicate.StatusMessage);
    }

    private static ReceiptReturnsWorkflowService CreateService(
        IRemoteOrderHistoryService? remote = null,
        FakeLocalOrderRepository? localRepository = null,
        LocalSellableItemIndex? priceIndex = null)
    {
        localRepository ??= new FakeLocalOrderRepository([]);
        return new ReceiptReturnsWorkflowService(
            new FakeReceiptQueryService(localRepository),
            localRepository,
            remote,
            priceIndex ?? new LocalSellableItemIndex(),
            new PosCartService());
    }

    private static PosSessionState CreateOnlineSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C01", "Alice", true, 0);
    }

    private static OrderHistoryDetailsDto CreateRemoteOrder(Guid orderGuid, Guid lineGuid, decimal quantity, decimal actualAmount)
    {
        return new OrderHistoryDetailsDto(
            orderGuid,
            "S001",
            "POS-01",
            "Alice",
            DateTimeOffset.UtcNow,
            actualAmount,
            0m,
            actualAmount,
            [
                new OrderHistoryLineDto(lineGuid, "SKU-001", "REF-001", "Milk", "690001", "ITEM-001", quantity, 10m, 0m, actualAmount)
            ],
            [new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, actualAmount, null)]);
    }

    private static LocalOrder CreateLocalOrder(Guid orderGuid)
    {
        return new LocalOrder(
            orderGuid,
            "S001",
            "POS-01",
            "C01",
            "Alice",
            DateTimeOffset.UtcNow,
            10m,
            0m,
            10m,
            [
                new LocalOrderLine(Guid.NewGuid(), "SKU-001", "REF-001", "Milk", "690001", "ITEM-001", 1m, 10m, 0m, 10m, PriceSourceKind.StoreRetailPrice)
            ],
            [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, 10m, null)]);
    }

    private static SellableItemDto CreateItem(
        string productCode = "SKU-001",
        string displayName = "Milk",
        string lookupCode = "690001",
        decimal retailPrice = 10m)
    {
        return new SellableItemDto(
            "S001",
            productCode,
            "REF-001",
            displayName,
            lookupCode,
            lookupCode,
            lookupCode,
            retailPrice,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            1m,
            DateTimeOffset.UtcNow);
    }

    private sealed class FakeReceiptQueryService(FakeLocalOrderRepository repository) : IReceiptQueryService
    {
        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return repository.GetRecentOrdersAsync(take, cancellationToken);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return repository.GetRecentOrdersAsync(query, take, cancellationToken);
        }

        public Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptDetails?>(null);
        }

        public Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptDetails?>(null);
        }
    }

    private sealed class FakeLocalOrderRepository(IEnumerable<LocalOrder> orders) : ILocalOrderRepository
    {
        private readonly Dictionary<Guid, LocalOrder> _orders = orders.ToDictionary(order => order.OrderGuid);

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            _orders[order.OrderGuid] = order;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return GetRecentOrdersAsync(new LocalOrderHistoryQuery(), take, cancellationToken);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>(_orders.Values
                .OrderByDescending(order => order.SoldAt)
                .Take(take)
                .Select(order => new LocalOrderSummary(order.OrderGuid, order.StoreCode, order.DeviceCode, order.CashierName, order.SoldAt, order.TotalAmount, order.DiscountAmount, order.ActualAmount, "Pending", order.Lines.Count, "Cash"))
                .ToList());
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_orders.TryGetValue(orderGuid, out var order) ? order : null);
        }
    }

    private sealed class FakeRemoteOrderHistoryService : IRemoteOrderHistoryService
    {
        public RemoteOrderHistoryResult QueryResult { get; init; } = new([]);

        public OrderReturnContextDto? ReturnContext { get; init; }

        public Task<RemoteOrderHistoryResult> QueryAsync(RemoteOrderHistoryQuery query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(QueryResult);
        }

        public Task<ReceiptDetails?> GetDetailsAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptDetails?>(null);
        }

        public Task<OrderReturnContextDto?> GetReturnContextAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ReturnContext);
        }

        public Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(OrderReturnRecordCreateRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OrderReturnRecordCreateResponse(request.ReturnOrderGuid, []));
        }
    }

    private sealed class ThrowingRemoteOrderHistoryService : IRemoteOrderHistoryService
    {
        public Task<RemoteOrderHistoryResult> QueryAsync(RemoteOrderHistoryQuery query, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Remote unavailable.");
        }

        public Task<ReceiptDetails?> GetDetailsAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Remote unavailable.");
        }

        public Task<OrderReturnContextDto?> GetReturnContextAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Remote unavailable.");
        }

        public Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(OrderReturnRecordCreateRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Remote unavailable.");
        }
    }
}
